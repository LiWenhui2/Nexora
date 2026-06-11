using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NaiwaProxy.Dialogs;
using NaiwaProxy.Models;
using NaiwaProxy.Services;

namespace NaiwaProxy;

public partial class MainWindow : Window
{
    private readonly SettingsStore _settingsStore = new();
    private readonly CoreService _coreService = new();
    private readonly ObservableCollection<VmessProfile> _profiles = [];
    private readonly DispatcherTimer _trafficTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private ICollectionView? _profilesView;
    private AppSettings _settings = new();
    private CancellationTokenSource? _latencyTestCancellation;
    private TrafficSnapshot? _lastTrafficSnapshot;
    private DateTime _lastTrafficSampleAt;
    private bool _isRefreshingTraffic;
    private DateTime _lastTrafficPersistAt;
    private bool _suppressProxyToggleEvent;
    private bool _suppressSystemProxyComboEvent;
    private bool _suppressRoutingComboEvent;
    private bool _suppressTunToggleEvent;
    private bool _isUiReady;

    public MainWindow()
    {
        InitializeComponent();
        LoadBrandIcon();
        _trafficTimer.Tick += TrafficTimer_Tick;
        _profilesView = CollectionViewSource.GetDefaultView(_profiles);
        _profilesView.Filter = FilterProfile;
        ProfilesGrid.ItemsSource = _profilesView;
        SearchBox.Text = string.Empty;
        _isUiReady = true;
        LoadSettings();
        RefreshHealthOverview();
        Loaded += MainWindow_Loaded;
        Closed += (_, _) =>
        {
            _latencyTestCancellation?.Cancel();
            _latencyTestCancellation?.Dispose();
            _trafficTimer.Stop();
            _settingsStore.Save(_settings);
            TunService.Stop();
            _coreService.Stop();
            ApplySystemProxyMode("Clear", save: false);
        };
    }

    private void LoadBrandIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "assets", "app-icon.png");
        if (!File.Exists(iconPath))
        {
            iconPath = Path.Combine(AppContext.BaseDirectory, "app-icon.png");
        }

        if (!File.Exists(iconPath))
        {
            return;
        }

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri(iconPath, UriKind.Absolute);
        bitmap.EndInit();
        bitmap.Freeze();
        BrandIconImage.Source = bitmap;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_Loaded;
        if (_profiles.Count == 0)
        {
            return;
        }

        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        await RunTcpLatencyTestsAsync(_profiles.ToList(), parallel: true);
    }

    private void LoadSettings()
    {
        _settings = _settingsStore.Load();
        _profiles.Clear();
        foreach (var profile in _settings.Profiles)
        {
            _profiles.Add(profile);
        }

        NodePickerCombo.ItemsSource = _profiles;
        SelectSystemProxyCombo(_settings.SystemProxyMode);
        SelectRoutingCombo(_settings.RoutingMode);

        var selected = _profiles.FirstOrDefault(p => p.Id == _settings.SelectedProfileId) ?? _profiles.FirstOrDefault();
        ProfilesGrid.SelectedItem = selected;
        NodePickerCombo.SelectedItem = selected;
        SyncTunToggleFromSettings();
        SyncProxyToggleFromCoreState();
        UpdateActiveProfileMarkers(_settings.SelectedProfileId);
        UpdateNodeStatusBar(selected);
        UpdateSidebarStatus();
    }

    private bool FilterProfile(object item)
    {
        if (item is not VmessProfile profile)
        {
            return false;
        }

        var keyword = SearchBox?.Text?.Trim();
        if (string.IsNullOrWhiteSpace(keyword))
        {
            return true;
        }

        return profile.DisplayName.Contains(keyword, StringComparison.OrdinalIgnoreCase)
            || profile.Endpoint.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        RefreshProfilesView();
    }

    private void FilterChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshProfilesView();
    }

    private void RefreshProfilesView()
    {
        if (!_isUiReady || _profilesView is null)
        {
            return;
        }

        _profilesView.Refresh();
    }

    private void ProfilesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProfilesGrid.SelectedItem is VmessProfile profile)
        {
            NodePickerCombo.SelectedItem = profile;
            _settings.SelectedProfileId = profile.Id;
            _settingsStore.Save(_settings);
            UpdateActiveProfileMarkers(profile.Id);
            UpdateNodeStatusBar(profile);
        }
    }

    private void NodePickerCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NodePickerCombo.SelectedItem is VmessProfile profile)
        {
            ProfilesGrid.SelectedItem = profile;
            _settings.SelectedProfileId = profile.Id;
            _settingsStore.Save(_settings);
            UpdateActiveProfileMarkers(profile.Id);
            UpdateNodeStatusBar(profile);
        }
    }

    private void UpdateNodeStatusBar(VmessProfile? profile)
    {
        if (profile is null)
        {
            NodeAddressText.Text = "[VMess] -";
            CurrentTcpLatencyText.Text = "TCP -";
            NodeAvailabilityText.Text = "无节点";
            return;
        }

        NodeAddressText.Text = profile.NodeAddressDisplay;
        CurrentTcpLatencyText.Text = $"TCP {profile.TcpLatencyDisplay}";
        NodeAvailabilityText.Text = profile.TcpLatencyDisplay switch
        {
            "Timeout" => "不可用",
            "-" or "..." => "待测速",
            _ when profile.TcpLatencyMs is not null => "可用",
            _ => "待测速"
        };

        var tagBackground = NodeAvailabilityText.Text switch
        {
            "可用" => "#DCFCE7",
            "不可用" => "#FEE2E2",
            _ => "#FEF3C7"
        };
        var tagBorder = NodeAvailabilityText.Text switch
        {
            "可用" => "#86EFAC",
            "不可用" => "#FCA5A5",
            _ => "#FDE68A"
        };
        var tagForeground = NodeAvailabilityText.Text switch
        {
            "可用" => "#166534",
            "不可用" => "#991B1B",
            _ => "#92400E"
        };
        NodeAvailabilityTag.Background = (SolidColorBrush)new BrushConverter().ConvertFromString(tagBackground)!;
        NodeAvailabilityTag.BorderBrush = (SolidColorBrush)new BrushConverter().ConvertFromString(tagBorder)!;
        NodeAvailabilityText.Foreground = (SolidColorBrush)new BrushConverter().ConvertFromString(tagForeground)!;

        HealthNodeText.Text = NodeAvailabilityText.Text == "可用"
            ? $"可用 · {profile.TcpLatencyMs} ms"
            : NodeAvailabilityText.Text;
        SideNodeText.Text = $"节点：{profile.DisplayName} · {profile.ProtocolDisplay}";
    }

    private void UpdateActiveProfileMarkers(string? activeId)
    {
        foreach (var profile in _profiles)
        {
            profile.SetActive(profile.Id == activeId);
        }

        ProfilesGrid.Items.Refresh();
    }

    private void UpdateSidebarStatus()
    {
        var running = _coreService.IsRunning;
        SideStatusDot.Fill = running ? new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A)) : new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8));
        SideStatusText.Text = running ? "运行中 · 系统代理已配置" : "已停止";
        SidePortsText.Text = $"HTTP 127.0.0.1:{_settings.HttpPort} · SOCKS {_settings.SocksPort}";
        SideCoreText.Text = running ? "Core 运行中" : "Core 未运行";
        ProxyStateText.Text = running ? "运行中" : "已停止";
        ProxyStateText.Foreground = running
            ? new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A))
            : new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B));
        ProxyToggleBorder.BorderBrush = running
            ? new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24))
            : new SolidColorBrush(Color.FromRgb(0xE2, 0xE8, 0xF0));
        ProxyToggleBorder.Background = running
            ? new SolidColorBrush(Color.FromRgb(0xFE, 0xF9, 0xC3))
            : new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
    }

    private void SyncProxyToggleFromCoreState()
    {
        _suppressProxyToggleEvent = true;
        ProxyToggle.IsChecked = _coreService.IsRunning;
        _suppressProxyToggleEvent = false;
        UpdateSidebarStatus();
    }

    private void SyncTunToggleFromSettings()
    {
        _suppressTunToggleEvent = true;
        TunToggle.IsChecked = _settings.IsTunEnabled;
        TunStateText.Text = TunService.IsRunning ? "运行中" : _settings.IsTunEnabled ? "待启动" : "已关闭";
        TunStateText.Foreground = TunService.IsRunning ? GreenBrush() : new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B));
        _suppressTunToggleEvent = false;
    }

    private void StartTunIfEnabled()
    {
        if (!_settings.IsTunEnabled)
        {
            SyncTunToggleFromSettings();
            return;
        }

        TunService.Start(_settings);
        SyncTunToggleFromSettings();
    }

    private void StartTrafficMonitor()
    {
        _lastTrafficSnapshot = null;
        _lastTrafficSampleAt = DateTime.Now;
        _lastTrafficPersistAt = DateTime.Now;
        StatDownloadText.Text = "0 B/s";
        StatUploadText.Text = "0 B/s";
        StatMonthText.Text = FormatBytes(_settings.TotalDownlinkBytes + _settings.TotalUplinkBytes);
        StatConnectionsText.Text = "统计中";
        TrafficBadgeText.Text = "下行 0 B/s · 上传 0 B/s";
        _trafficTimer.Start();
        _ = RefreshTrafficAsync();
    }

    private void StopTrafficMonitor()
    {
        _trafficTimer.Stop();
        _lastTrafficSnapshot = null;
        StatDownloadText.Text = "—";
        StatUploadText.Text = "—";
        StatMonthText.Text = FormatBytes(_settings.TotalDownlinkBytes + _settings.TotalUplinkBytes);
        StatConnectionsText.Text = "—";
        TrafficBadgeText.Text = "下行 — · 上传 —";
    }

    private async void TrafficTimer_Tick(object? sender, EventArgs e)
    {
        await RefreshTrafficAsync();
    }

    private async Task RefreshTrafficAsync()
    {
        if (!_coreService.IsRunning || _isRefreshingTraffic)
        {
            return;
        }

        _isRefreshingTraffic = true;
        try
        {
            var snapshot = await TrafficStatsService.QueryAsync(_settings);
            var now = DateTime.Now;
            var seconds = Math.Max((now - _lastTrafficSampleAt).TotalSeconds, 1);

            var downSpeed = 0d;
            var upSpeed = 0d;
            if (_lastTrafficSnapshot is not null)
            {
                var downDelta = Math.Max(0, snapshot.DownlinkBytes - _lastTrafficSnapshot.DownlinkBytes);
                var upDelta = Math.Max(0, snapshot.UplinkBytes - _lastTrafficSnapshot.UplinkBytes);
                downSpeed = downDelta / seconds;
                upSpeed = upDelta / seconds;
                _settings.TotalDownlinkBytes += downDelta;
                _settings.TotalUplinkBytes += upDelta;
            }

            _lastTrafficSnapshot = snapshot;
            _lastTrafficSampleAt = now;

            StatDownloadText.Text = $"{FormatBytes(downSpeed)}/s";
            StatUploadText.Text = $"{FormatBytes(upSpeed)}/s";
            StatMonthText.Text = FormatBytes(_settings.TotalDownlinkBytes + _settings.TotalUplinkBytes);
            StatConnectionsText.Text = _coreService.IsRunning ? "运行中" : "—";
            TrafficBadgeText.Text = $"下行 {FormatBytes(downSpeed)}/s · 上传 {FormatBytes(upSpeed)}/s";

            if ((now - _lastTrafficPersistAt).TotalSeconds >= 5)
            {
                _settingsStore.Save(_settings);
                _lastTrafficPersistAt = now;
            }
        }
        catch
        {
            StatConnectionsText.Text = "统计不可用";
        }
        finally
        {
            _isRefreshingTraffic = false;
        }
    }

    private static string FormatBytes(double bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.##} {units[unit]}";
    }

    private async void ProxyToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!_isUiReady || _suppressProxyToggleEvent)
        {
            return;
        }

        if (ProxyToggle.IsChecked == true)
        {
            await StartProxyAsync();
        }
        else
        {
            StopProxy();
        }
    }

    private void TunToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!_isUiReady || _suppressTunToggleEvent)
        {
            return;
        }

        if (TunToggle.IsChecked == true)
        {
            try
            {
                if (!TunService.IsAdministrator())
                {
                    _settings.IsTunEnabled = true;
                    _settingsStore.Save(_settings);
                    RelaunchAsAdministrator();
                    return;
                }

                TunService.EnsureCanEnable();
                if (!_coreService.IsRunning)
                {
                    throw new InvalidOperationException("请先启用代理，TUN 会转发到本地 SOCKS 端口。");
                }

                TunService.Start(_settings);
                _settings.IsTunEnabled = true;
                _settingsStore.Save(_settings);
            }
            catch (Exception ex)
            {
                _settings.IsTunEnabled = false;
                _suppressTunToggleEvent = true;
                TunToggle.IsChecked = false;
                _suppressTunToggleEvent = false;
                ShowError(ex);
            }
        }
        else
        {
            TunService.Stop();
            _settings.IsTunEnabled = false;
            _settingsStore.Save(_settings);
        }

        SyncTunToggleFromSettings();
        RefreshHealthOverview();
    }

    private void RelaunchAsAdministrator()
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath))
        {
            throw new InvalidOperationException("无法定位当前程序路径，不能请求管理员权限。");
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true,
                Verb = "runas"
            });
            Application.Current.Shutdown();
        }
        catch (Win32Exception)
        {
            _settings.IsTunEnabled = false;
            _settingsStore.Save(_settings);
            throw new InvalidOperationException("已取消管理员权限请求，TUN 模式未启用。");
        }
    }

    private async Task StartProxyAsync()
    {
        try
        {
            var profile = GetSelectedProfile();
            await _coreService.StartAsync(_settings, profile);
            SaveProfiles(profile.Id);
            ApplySystemProxyMode(_settings.SystemProxyMode, save: false);
            StartTunIfEnabled();
            StartTrafficMonitor();
            UpdateSidebarStatus();
            RefreshHealthOverview();
        }
        catch (Exception ex)
        {
            _suppressProxyToggleEvent = true;
            ProxyToggle.IsChecked = false;
            _suppressProxyToggleEvent = false;
            ShowError(ex);
        }

    }

    private void StopProxy()
    {
        _coreService.Stop();
        TunService.Stop();
        _settingsStore.Save(_settings);
        StopTrafficMonitor();
        if (_settings.SystemProxyMode is "Auto" or "Clear" or "Pac")
        {
            ApplySystemProxyMode("Clear", save: false);
        }

        UpdateSidebarStatus();
        RefreshHealthOverview();
    }

    private async Task RestartCoreAsync()
    {
        if (!_coreService.IsRunning)
        {
            return;
        }

        var profile = GetSelectedProfile();
        TunService.Stop();
        await _coreService.StartAsync(_settings, profile);
        ApplySystemProxyMode(_settings.SystemProxyMode, save: false);
        StartTunIfEnabled();
        StartTrafficMonitor();
        UpdateSidebarStatus();
        RefreshHealthOverview();
    }

    private void SystemProxyCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isUiReady || _suppressSystemProxyComboEvent)
        {
            return;
        }

        var mode = GetSystemProxyModeFromCombo();
        ApplySystemProxyMode(mode, save: true);
        RefreshHealthOverview();
    }

    private async void RoutingCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isUiReady || _suppressRoutingComboEvent)
        {
            return;
        }

        _settings.RoutingMode = GetRoutingModeFromCombo();
        _settingsStore.Save(_settings);

        if (_coreService.IsRunning)
        {
            try
            {
                await RestartCoreAsync();
            }
            catch (Exception ex)
            {
                ShowError(ex);
            }
        }
    }

    private async void EditRoutingButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new CustomRoutingDialog(_settings.CustomRouting) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        _settings.CustomRouting = dialog.Routing;
        _settings.RoutingMode = "Custom";
        _settingsStore.Save(_settings);
        SelectRoutingCombo(_settings.RoutingMode);

        if (_coreService.IsRunning)
        {
            try
            {
                await RestartCoreAsync();
            }
            catch (Exception ex)
            {
                ShowError(ex);
            }
        }
    }

    private void ApplySystemProxyMode(string mode, bool save)
    {
        _settings.SystemProxyMode = mode;
        if (save)
        {
            _settingsStore.Save(_settings);
        }

        try
        {
            switch (mode)
            {
                case "Clear":
                    SystemProxyService.DisableProxy();
                    break;
                case "Auto":
                    if (_coreService.IsRunning)
                    {
                        SystemProxyService.EnableHttpProxy(_settings.HttpPort);
                    }
                    else
                    {
                        SystemProxyService.DisableProxy();
                    }
                    break;
                case "Unchanged":
                    break;
                case "Pac":
                    if (_coreService.IsRunning)
                    {
                        SystemProxyService.EnablePacProxy(_settings.HttpPort);
                    }
                    else
                    {
                        SystemProxyService.DisableProxy();
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }

        UpdateSidebarStatus();
    }

    private string GetSystemProxyModeFromCombo()
    {
        return (SystemProxyCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() switch
        {
            "清除系统代理" => "Clear",
            "自动配置系统代理" => "Auto",
            "不改变系统代理" => "Unchanged",
            "PAC 模式" => "Pac",
            _ => "Auto"
        };
    }

    private void SelectSystemProxyCombo(string mode)
    {
        _suppressSystemProxyComboEvent = true;
        var label = mode switch
        {
            "Clear" => "清除系统代理",
            "Auto" => "自动配置系统代理",
            "Unchanged" => "不改变系统代理",
            "Pac" => "PAC 模式",
            _ => "自动配置系统代理"
        };

        foreach (ComboBoxItem item in SystemProxyCombo.Items)
        {
            if (string.Equals(item.Content?.ToString(), label, StringComparison.Ordinal))
            {
                SystemProxyCombo.SelectedItem = item;
                break;
            }
        }

        _suppressSystemProxyComboEvent = false;
    }

    private string GetRoutingModeFromCombo()
    {
        return (RoutingCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() switch
        {
            "全局代理" => "Global",
            "V4-绕过大陆 (Whitelist)" => "BypassChina",
            "绕过局域网" => "BypassLan",
            "直连模式" => "Direct",
            "自定义规则" => "Custom",
            _ => "BypassChina"
        };
    }

    private void SelectRoutingCombo(string mode)
    {
        _suppressRoutingComboEvent = true;
        var label = mode switch
        {
            "Global" => "全局代理",
            "BypassChina" => "V4-绕过大陆 (Whitelist)",
            "BypassLan" => "绕过局域网",
            "Direct" => "直连模式",
            "Custom" => "自定义规则",
            _ => "V4-绕过大陆 (Whitelist)"
        };

        foreach (ComboBoxItem item in RoutingCombo.Items)
        {
            if (string.Equals(item.Content?.ToString(), label, StringComparison.Ordinal))
            {
                RoutingCombo.SelectedItem = item;
                break;
            }
        }

        _suppressRoutingComboEvent = false;
    }

    private void RefreshHealthOverview()
    {
        var corePath = Path.Combine(AppContext.BaseDirectory, "cores", _settings.CoreExecutable);
        HealthCoreText.Text = File.Exists(corePath) ? "正常" : "缺失";
        HealthCoreText.Foreground = File.Exists(corePath) ? GreenBrush() : RedBrush();

        var geoIp = Path.Combine(AppContext.BaseDirectory, "cores", "geoip.dat");
        var geoSite = Path.Combine(AppContext.BaseDirectory, "cores", "geosite.dat");
        var geoReady = File.Exists(geoIp) && File.Exists(geoSite);
        HealthGeoText.Text = geoReady ? "已就绪" : "缺失";
        HealthGeoText.Foreground = geoReady ? GreenBrush() : YellowBrush();

        HealthPortText.Text = _coreService.IsRunning
            ? $"{_settings.SocksPort}/{_settings.HttpPort} 正常"
            : "Core 未运行";
        HealthPortText.Foreground = _coreService.IsRunning ? GreenBrush() : YellowBrush();

        HealthProxyText.Text = _settings.SystemProxyMode switch
        {
            "Auto" when _coreService.IsRunning => "一致",
            "Pac" when _coreService.IsRunning => "PAC 已启用",
            "Clear" => "已清除",
            "Unchanged" => "未修改",
            _ => "待确认"
        };
        HealthProxyText.Foreground = HealthProxyText.Text is "一致" or "PAC 已启用" ? GreenBrush() : YellowBrush();

        HealthTunText.Text = TunService.IsRunning ? "运行中" : _settings.IsTunEnabled ? "待启动" : TunService.GetStatusText();
        HealthTunText.Foreground = HealthTunText.Text is "运行中" or "可启用" ? GreenBrush() : YellowBrush();
    }

    private static SolidColorBrush GreenBrush() => new(Color.FromRgb(0x16, 0xA3, 0x4A));
    private static SolidColorBrush RedBrush() => new(Color.FromRgb(0xDC, 0x26, 0x26));
    private static SolidColorBrush YellowBrush() => new(Color.FromRgb(0xCA, 0x8A, 0x04));

    private void ProfilesGrid_PreviewMouseRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var row = ItemsControl.ContainerFromElement(ProfilesGrid, e.OriginalSource as DependencyObject) as DataGridRow;
        if (row?.Item is VmessProfile profile)
        {
            ProfilesGrid.SelectedItem = profile;
        }
    }

    private async void CtxSetActiveNode_Click(object sender, RoutedEventArgs e)
    {
        if (ProfilesGrid.SelectedItem is VmessProfile profile)
        {
            NodePickerCombo.SelectedItem = profile;
            _settings.SelectedProfileId = profile.Id;
            SaveProfiles(profile.Id);
            if (_coreService.IsRunning)
            {
                await RestartCoreAsync();
            }
        }
    }

    private void CtxEditNode_Click(object sender, RoutedEventArgs e)
    {
        OpenEditDialog(ProfilesGrid.SelectedItem as VmessProfile);
    }

    private void CtxDeleteNode_Click(object sender, RoutedEventArgs e)
    {
        if (ProfilesGrid.SelectedItem is not VmessProfile profile)
        {
            return;
        }

        if (MessageBox.Show($"确定删除节点「{profile.DisplayName}」？", "NaiwaProxy", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        _profiles.Remove(profile);
        SaveProfiles(_profiles.FirstOrDefault()?.Id);
        RefreshNodePicker();
        ProfilesGrid.SelectedItem = _profiles.FirstOrDefault();
    }

    private void CtxTcpTest_Click(object sender, RoutedEventArgs e)
    {
        if (ProfilesGrid.SelectedItem is VmessProfile profile)
        {
            _ = RunTcpLatencyTestsAsync([profile], parallel: true);
        }
    }

    private void OpenEditDialog(VmessProfile? profile)
    {
        var dialog = new NodeEditDialog(profile) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var saved = dialog.Profile;
        var existing = _profiles.FirstOrDefault(p => p.Id == saved.Id);
        if (existing is null)
        {
            _profiles.Add(saved);
        }
        else
        {
            CopyProfile(saved, existing);
            existing.ResetLatency();
        }

        SaveProfiles(saved.Id);
        RefreshNodePicker();
        ProfilesGrid.Items.Refresh();
        ProfilesGrid.SelectedItem = _profiles.FirstOrDefault(p => p.Id == saved.Id);
    }

    private void OpenImportDialog()
    {
        var dialog = new ImportDialog { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        foreach (var profile in dialog.ImportedProfiles)
        {
            _profiles.Add(profile);
        }

        var last = dialog.ImportedProfiles.LastOrDefault();
        SaveProfiles(last?.Id ?? _settings.SelectedProfileId);
        RefreshNodePicker();
        ProfilesGrid.Items.Refresh();
        if (last is not null)
        {
            ProfilesGrid.SelectedItem = last;
        }
    }

    private void RefreshNodePicker()
    {
        NodePickerCombo.ItemsSource = null;
        NodePickerCombo.ItemsSource = _profiles;
    }

    private void CtxNewNode_Click(object sender, RoutedEventArgs e) => OpenEditDialog(null);

    private void CtxImportNode_Click(object sender, RoutedEventArgs e) => OpenImportDialog();

    private void SubscriptionNavButton_Click(object sender, RoutedEventArgs e) => OpenImportDialog();

    private void CtxExportNode_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show("节点导出功能将在后续版本提供。", "NaiwaProxy", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void CtxMaintainNode_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show("节点维护（去重、清理不可用节点等）将在后续版本提供。", "NaiwaProxy", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void CtxAllTcpTest_Click(object sender, RoutedEventArgs e)
    {
        if (_profiles.Count == 0)
        {
            return;
        }

        _ = RunTcpLatencyTestsAsync(_profiles.ToList(), parallel: true);
    }

    private async void TestTcpLatencyButton_Click(object sender, RoutedEventArgs e)
    {
        if (ProfilesGrid.SelectedItem is not VmessProfile profile)
        {
            MessageBox.Show("请先选择一个节点。", "NaiwaProxy", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await RunTcpLatencyTestsAsync([profile], parallel: true);
    }

    private async void SwitchFastestButton_Click(object sender, RoutedEventArgs e)
    {
        var fastest = _profiles
            .Where(p => p.TcpLatencyMs is not null)
            .OrderBy(p => p.TcpLatencyMs)
            .FirstOrDefault();

        if (fastest is null)
        {
            MessageBox.Show("没有可用的测速结果，请先执行 TCP 测速。", "NaiwaProxy", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        ProfilesGrid.SelectedItem = fastest;
        NodePickerCombo.SelectedItem = fastest;
        if (_coreService.IsRunning)
        {
            await RestartCoreAsync();
        }
    }

    private async Task RunTcpLatencyTestsAsync(IReadOnlyList<VmessProfile> profiles, bool parallel)
    {
        _latencyTestCancellation?.Cancel();
        _latencyTestCancellation?.Dispose();
        _latencyTestCancellation = new CancellationTokenSource();
        var cancellationToken = _latencyTestCancellation.Token;

        SetLatencyTestingEnabled(false);

        try
        {
            if (parallel)
            {
                await Task.WhenAll(profiles.Select(profile => TestTcpLatencyAsync(profile, cancellationToken)));
            }
            else
            {
                foreach (var profile in profiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await TestTcpLatencyAsync(profile, cancellationToken);
                }
            }

            if (ProfilesGrid.SelectedItem is VmessProfile selected)
            {
                UpdateNodeStatusBar(selected);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            SetLatencyTestingEnabled(true);
            RefreshHealthOverview();
        }
    }

    private static async Task TestTcpLatencyAsync(VmessProfile profile, CancellationToken cancellationToken)
    {
        profile.BeginTcpLatencyTest();
        var latency = await LatencyTestService.MeasureTcpAsync(profile.Address, profile.Port, cancellationToken: cancellationToken);
        if (!cancellationToken.IsCancellationRequested)
        {
            profile.CompleteTcpLatencyTest(latency);
        }
    }

    private void SetLatencyTestingEnabled(bool enabled)
    {
        TestTcpLatencyButton.IsEnabled = enabled;
        SwitchFastestButton.IsEnabled = enabled;
        EditRoutingButton.IsEnabled = enabled;
    }

    private VmessProfile GetSelectedProfile()
    {
        return ProfilesGrid.SelectedItem as VmessProfile
            ?? NodePickerCombo.SelectedItem as VmessProfile
            ?? throw new InvalidOperationException("请先选择一个节点。");
    }

    private void SaveProfiles(string? selectedProfileId)
    {
        _settings.Profiles = _profiles.ToList();
        _settings.SelectedProfileId = selectedProfileId;
        _settingsStore.Save(_settings);
        UpdateActiveProfileMarkers(selectedProfileId);
    }

    private static void CopyProfile(VmessProfile source, VmessProfile target)
    {
        target.Name = source.Name;
        target.Address = source.Address;
        target.Port = source.Port;
        target.UserId = source.UserId;
        target.AlterId = source.AlterId;
        target.Security = source.Security;
        target.Network = source.Network;
        target.Type = source.Type;
        target.Host = source.Host;
        target.Path = source.Path;
        target.Tls = source.Tls;
        target.Sni = source.Sni;
        target.Remark = source.Remark;
        target.SubscriptionName = source.SubscriptionName;
        target.SubscriptionUpdatedAt = source.SubscriptionUpdatedAt;
    }

    private static void ShowError(Exception exception)
    {
        MessageBox.Show(exception.Message, "NaiwaProxy", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
