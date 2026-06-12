using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using NaiwaProxy.Dialogs;
using NaiwaProxy.Models;
using NaiwaProxy.Services;
using QRCoder;

namespace NaiwaProxy;

public partial class MainWindow : Window
{
    private const string ProjectUrl = "https://github.com/LiWenhui2/NaiwaProxy";
    private const string LatestReleaseApi = "https://api.github.com/repos/LiWenhui2/NaiwaProxy/releases/latest";
    private static readonly HttpClient UpdateHttpClient = new();
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
        AboutIconImage.Source = bitmap;
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
        UpdateRoutingEditorVisibility();

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
        if (!string.IsNullOrWhiteSpace(keyword) &&
            !profile.DisplayName.Contains(keyword, StringComparison.OrdinalIgnoreCase) &&
            !profile.Endpoint.Contains(keyword, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var protocol = (ProtocolFilterCombo?.SelectedItem as ComboBoxItem)?.Content?.ToString();
        if (!string.IsNullOrWhiteSpace(protocol) &&
            protocol != "全部协议" &&
            !string.Equals(profile.ProtocolDisplay, protocol, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (AvailableOnlyCheck?.IsChecked == true && profile.TcpLatencyMs is null)
        {
            return false;
        }

        return true;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        RefreshProfilesView();
    }

    private void FilterChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshProfilesView();
    }

    private void AvailabilityFilterChanged(object sender, RoutedEventArgs e)
    {
        RefreshProfilesView();
    }

    private void RefreshProfilesView()
    {
        if (!_isUiReady || _profilesView is null)
        {
            return;
        }

        ApplyProfilesSort();
        _profilesView.Refresh();
    }

    private void ApplyProfilesSort()
    {
        if (_profilesView is null)
        {
            return;
        }

        _profilesView.SortDescriptions.Clear();
        var sort = (SortCombo?.SelectedItem as ComboBoxItem)?.Content?.ToString();
        if (sort == "延迟优先")
        {
            _profilesView.SortDescriptions.Add(new SortDescription(nameof(VmessProfile.TcpLatencyMs), ListSortDirection.Ascending));
            _profilesView.SortDescriptions.Add(new SortDescription(nameof(VmessProfile.DisplayName), ListSortDirection.Ascending));
        }
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
        UpdateRoutingEditorVisibility();

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
        UpdateRoutingEditorVisibility();

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
        UpdateRoutingEditorVisibility();
    }

    private void UpdateRoutingEditorVisibility()
    {
        if (EditRoutingButton is null)
        {
            return;
        }

        EditRoutingButton.Visibility = _settings.RoutingMode == "Custom" ? Visibility.Visible : Visibility.Collapsed;
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

    private void ProfilesGrid_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        NodePageScroll.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = MouseWheelEvent
        });
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

    private void CtxNewNode_Click(object sender, RoutedEventArgs e) => ShowNewNodePage();

    private void CtxImportNode_Click(object sender, RoutedEventArgs e) => ShowImportPage();

    private void SubscriptionNavButton_Click(object sender, RoutedEventArgs e) => ShowImportPage();

    private void NodeListNavButton_Click(object sender, RoutedEventArgs e) => ShowNodePage();

    private void NewNodeNavButton_Click(object sender, RoutedEventArgs e) => ShowNewNodePage();

    private void ImportNodeNavButton_Click(object sender, RoutedEventArgs e) => ShowImportPage();

    private void ExportNodeNavButton_Click(object sender, RoutedEventArgs e) => ShowExportPage();

    private void UpdateNavButton_Click(object sender, RoutedEventArgs e) => ShowUpdatePage();

    private void GithubNavButton_Click(object sender, RoutedEventArgs e) => OpenPath(ProjectUrl);

    private void AboutNavButton_Click(object sender, RoutedEventArgs e)
    {
        ShowAboutPage();
    }

    private void ShowNodePage()
    {
        ShowPage(NodePageScroll, NodeListNavButton);
    }

    private void ShowNewNodePage()
    {
        InlineClearNodeForm();
        ShowPage(NewNodePageScroll, NewNodeNavButton);
    }

    private void ShowImportPage()
    {
        ShowPage(ImportPageScroll, ImportNodeNavButton);
    }

    private void ShowExportPage()
    {
        RefreshExportProfiles();
        ShowPage(ExportPageScroll, ExportNodeNavButton);
    }

    private void ShowUpdatePage()
    {
        UpdateStatusText.Text = "点击按钮检查 GitHub Release 中的最新版本。";
        ShowPage(UpdatePageScroll, UpdateNavButton);
    }

    private void ShowAboutPage()
    {
        LoadAboutPageInfo();
        ShowPage(AboutPageScroll, AboutNavButton);
    }

    private void ShowPage(ScrollViewer page, Button? activeButton)
    {
        NodePageScroll.Visibility = Visibility.Collapsed;
        NewNodePageScroll.Visibility = Visibility.Collapsed;
        ImportPageScroll.Visibility = Visibility.Collapsed;
        ExportPageScroll.Visibility = Visibility.Collapsed;
        UpdatePageScroll.Visibility = Visibility.Collapsed;
        AboutPageScroll.Visibility = Visibility.Collapsed;
        page.Visibility = Visibility.Visible;

        foreach (var button in new[] { NodeListNavButton, NewNodeNavButton, ImportNodeNavButton, ExportNodeNavButton, AboutNavButton, UpdateNavButton })
        {
            button.Style = ReferenceEquals(button, activeButton)
                ? (Style)FindResource("ActiveNavButtonStyle")
                : (Style)FindResource("NavChildButtonStyle");
        }
    }

    private void LoadAboutPageInfo()
    {
        var configDirectory = GetConfigDirectory();
        var runtimeDirectory = Path.Combine(AppContext.BaseDirectory, "cores");
        AboutAppVersionText.Text = GetCurrentVersion();
        AboutBuildTimeText.Text = GetBuildTime();
        AboutCoreVersionText.Text = GetExecutableVersion(CoreRunner.ResolveCorePath("xray.exe"), "version");
        AboutTunRuntimeText.Text = File.Exists(TunService.SingBoxPath)
            ? GetExecutableVersion(TunService.SingBoxPath, "version")
            : "未安装";
        AboutConfigDirectoryText.Text = configDirectory;
        AboutRuntimeDirectoryText.Text = runtimeDirectory;
        AboutLicenseText.Text = "NaiwaProxy: project license · Xray Core: MPL-2.0 · sing-box: GPL-3.0-or-later · Wintun: GPL-2.0 · Inno Setup: Inno Setup License";
    }

    private void AboutOpenConfigButton_Click(object sender, RoutedEventArgs e)
    {
        var directory = GetConfigDirectory();
        Directory.CreateDirectory(directory);
        OpenPath(directory);
    }

    private void AboutOpenRuntimeButton_Click(object sender, RoutedEventArgs e) => OpenPath(Path.Combine(AppContext.BaseDirectory, "cores"));

    private async void UpdatePageCheckButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            UpdateStatusText.Text = "正在检查更新...";
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
            request.Headers.UserAgent.ParseAdd("NaiwaProxy");
            using var response = await UpdateHttpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
            using var stream = await response.Content.ReadAsStreamAsync();
            using var document = await JsonDocument.ParseAsync(stream);
            var root = document.RootElement;
            var latestTag = root.GetProperty("tag_name").GetString() ?? "";
            var htmlUrl = root.GetProperty("html_url").GetString() ?? ProjectUrl;
            var currentVersion = GetCurrentVersion();
            if (CompareVersionText(latestTag, currentVersion) <= 0)
            {
                UpdateStatusText.Text = $"当前已是最新版本：{currentVersion}。GitHub 最新版本：{latestTag}";
                return;
            }

            UpdateStatusText.Text = $"发现新版本 {latestTag}。可点击“打开发布页”下载。";
            _latestReleaseUrl = htmlUrl;
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = $"检查更新失败：{ex.Message}";
        }
    }

    private string _latestReleaseUrl = ProjectUrl;

    private void UpdatePageReleaseButton_Click(object sender, RoutedEventArgs e) => OpenPath(_latestReleaseUrl);

    private void InlineClearNodeButton_Click(object sender, RoutedEventArgs e) => InlineClearNodeForm();

    private void InlineClearNodeForm()
    {
        InlineProtocolBox.SelectedIndex = 0;
        InlineNameBox.Text = "";
        InlineAddressBox.Text = "";
        InlinePortBox.Text = "443";
        InlineUserBox.Text = "";
        InlinePasswordBox.Text = "";
        InlineSecurityBox.Text = "auto";
        InlineNetworkBox.Text = "tcp";
        InlineHostBox.Text = "";
        InlineSniBox.Text = "";
        InlinePathBox.Text = "";
    }

    private void InlineSaveNodeButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!int.TryParse(InlinePortBox.Text.Trim(), out var port) || port is <= 0 or > 65535)
            {
                throw new InvalidOperationException("端口必须在 1 到 65535 之间。");
            }

            var protocol = (InlineProtocolBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "vmess";
            if ((protocol is "vmess" or "vless") && !Guid.TryParse(InlineUserBox.Text.Trim(), out _))
            {
                throw new InvalidOperationException("VMess/VLESS 节点需要填写有效 UUID。");
            }

            if (string.IsNullOrWhiteSpace(InlineAddressBox.Text))
            {
                throw new InvalidOperationException("地址不能为空。");
            }

            var profile = new VmessProfile
            {
                Protocol = protocol,
                Name = string.IsNullOrWhiteSpace(InlineNameBox.Text) ? $"{InlineAddressBox.Text.Trim()}:{port}" : InlineNameBox.Text.Trim(),
                Address = InlineAddressBox.Text.Trim(),
                Port = port,
                UserId = InlineUserBox.Text.Trim(),
                Password = InlinePasswordBox.Text.Trim(),
                Security = string.IsNullOrWhiteSpace(InlineSecurityBox.Text) ? "auto" : InlineSecurityBox.Text.Trim(),
                Network = string.IsNullOrWhiteSpace(InlineNetworkBox.Text) ? "tcp" : InlineNetworkBox.Text.Trim(),
                Host = InlineHostBox.Text.Trim(),
                Sni = InlineSniBox.Text.Trim(),
                Path = InlinePathBox.Text.Trim()
            };

            _profiles.Add(profile);
            SaveProfiles(profile.Id);
            RefreshNodePicker();
            ProfilesGrid.SelectedItem = profile;
            ShowNodePage();
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private void InlinePasteImportButton_Click(object sender, RoutedEventArgs e)
    {
        if (Clipboard.ContainsText())
        {
            InlineImportBox.Text = Clipboard.GetText();
        }
    }

    private async void InlineOpenImportFileButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择节点文本文件",
            Filter = "文本和配置文件|*.txt;*.conf;*.json;*.yaml;*.yml|所有文件|*.*"
        };
        if (dialog.ShowDialog(this) == true)
        {
            InlineImportBox.Text = await File.ReadAllTextAsync(dialog.FileName);
        }
    }

    private async void InlineImportButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var imported = await SubscriptionImportService.ImportAsync(InlineImportBox.Text);
            foreach (var profile in imported)
            {
                _profiles.Add(profile);
            }

            var last = imported.LastOrDefault();
            SaveProfiles(last?.Id ?? _settings.SelectedProfileId);
            RefreshNodePicker();
            ProfilesGrid.Items.Refresh();
            if (last is not null)
            {
                ProfilesGrid.SelectedItem = last;
            }

            ShowNodePage();
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private void RefreshExportProfiles()
    {
        ExportProfileCombo.ItemsSource = null;
        ExportProfileCombo.ItemsSource = _profiles;
        ExportProfileCombo.SelectedItem = ProfilesGrid.SelectedItem as VmessProfile ?? _profiles.FirstOrDefault();
        RefreshExportPreview();
    }

    private void ExportProfileCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshExportPreview();

    private void RefreshExportPreview()
    {
        if (ExportProfileCombo.SelectedItem is not VmessProfile profile)
        {
            ExportShareBox.Text = "";
            ExportQrImage.Source = null;
            return;
        }

        var link = BuildShareLink(profile);
        ExportShareBox.Text = link;
        ExportQrImage.Source = GenerateQrImage(link);
    }

    private void CopyExportLinkButton_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(ExportShareBox.Text))
        {
            Clipboard.SetText(ExportShareBox.Text);
        }
    }

    private void SaveExportLinkButton_Click(object sender, RoutedEventArgs e)
    {
        if (ExportProfileCombo.SelectedItem is not VmessProfile profile || string.IsNullOrWhiteSpace(ExportShareBox.Text))
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "导出节点",
            FileName = $"{SanitizeFileName(profile.DisplayName)}.txt",
            Filter = "文本文件|*.txt|所有文件|*.*"
        };
        if (dialog.ShowDialog(this) == true)
        {
            File.WriteAllText(dialog.FileName, ExportShareBox.Text, Encoding.UTF8);
        }
    }

    private void CtxExportNode_Click(object sender, RoutedEventArgs e)
    {
        ShowExportPage();
    }

    private void CtxMaintainNode_Click(object sender, RoutedEventArgs e)
    {
        if (_profiles.Count == 0)
        {
            MessageBox.Show("当前没有需要维护的节点。", "NaiwaProxy", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var result = MessageBox.Show("将执行：\n1. 按协议、地址、端口、账号/密码删除重复节点\n2. 删除已测速且超时的节点\n\n是否继续？", "节点维护", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        var before = _profiles.Count;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var selectedId = _settings.SelectedProfileId;
        var kept = _profiles
            .Where(profile => profile.TcpLatencyDisplay != "Timeout")
            .Where(profile => seen.Add(BuildProfileKey(profile)))
            .ToList();

        _profiles.Clear();
        foreach (var profile in kept)
        {
            _profiles.Add(profile);
        }

        if (_profiles.All(profile => profile.Id != selectedId))
        {
            selectedId = _profiles.FirstOrDefault()?.Id;
        }

        SaveProfiles(selectedId);
        RefreshNodePicker();
        RefreshProfilesView();
        ProfilesGrid.SelectedItem = _profiles.FirstOrDefault(profile => profile.Id == selectedId);
        MessageBox.Show($"维护完成：清理 {before - _profiles.Count} 个节点，保留 {_profiles.Count} 个节点。", "NaiwaProxy", MessageBoxButton.OK, MessageBoxImage.Information);
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
        target.Protocol = source.Protocol;
        target.Address = source.Address;
        target.Port = source.Port;
        target.UserId = source.UserId;
        target.Password = source.Password;
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

    private static string BuildProfileKey(VmessProfile profile)
    {
        return string.Join("|", profile.Protocol, profile.Address, profile.Port, profile.UserId, profile.Password, profile.Security);
    }

    private static BitmapImage GenerateQrImage(string value)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(value, QRCodeGenerator.ECCLevel.Q);
        var qr = new PngByteQRCode(data);
        var bytes = qr.GetGraphic(8);
        using var stream = new MemoryStream(bytes);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static string SanitizeFileName(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new string(value.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "NaiwaProxy-Node" : sanitized;
    }

    private static string BuildShareLink(VmessProfile profile)
    {
        return profile.Protocol.ToLowerInvariant() switch
        {
            "vless" => BuildVlessLink(profile),
            "trojan" => BuildTrojanLink(profile),
            "shadowsocks" or "ss" => BuildShadowsocksLink(profile),
            "socks" or "socks5" => BuildUserPassLink(profile, "socks"),
            "http" or "https" => BuildUserPassLink(profile, "http"),
            _ => BuildVmessLink(profile)
        };
    }

    private static string BuildVmessLink(VmessProfile profile)
    {
        var payload = new
        {
            v = "2",
            ps = profile.DisplayName,
            add = profile.Address,
            port = profile.Port.ToString(),
            id = profile.UserId,
            aid = profile.AlterId.ToString(),
            scy = profile.Security,
            net = profile.Network,
            type = profile.Type,
            host = profile.Host,
            path = profile.Path,
            tls = profile.Tls,
            sni = profile.Sni
        };
        return $"vmess://{Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)))}";
    }

    private static string BuildVlessLink(VmessProfile profile)
    {
        var query = BuildQuery([
            ("encryption", string.IsNullOrWhiteSpace(profile.Security) ? "none" : profile.Security),
            ("type", profile.Network),
            ("security", string.IsNullOrWhiteSpace(profile.Tls) ? "" : "tls"),
            ("sni", profile.Sni),
            ("host", profile.Host),
            ("path", profile.Path)
        ]);
        return $"vless://{Uri.EscapeDataString(profile.UserId)}@{profile.Address}:{profile.Port}{query}#{Uri.EscapeDataString(profile.DisplayName)}";
    }

    private static string BuildTrojanLink(VmessProfile profile)
    {
        var query = BuildQuery([
            ("type", profile.Network),
            ("security", string.IsNullOrWhiteSpace(profile.Tls) ? "tls" : "tls"),
            ("sni", profile.Sni),
            ("host", profile.Host),
            ("path", profile.Path)
        ]);
        return $"trojan://{Uri.EscapeDataString(profile.Password)}@{profile.Address}:{profile.Port}{query}#{Uri.EscapeDataString(profile.DisplayName)}";
    }

    private static string BuildShadowsocksLink(VmessProfile profile)
    {
        var method = string.IsNullOrWhiteSpace(profile.Security) ? "aes-128-gcm" : profile.Security;
        var userInfo = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{method}:{profile.Password}")).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"ss://{userInfo}@{profile.Address}:{profile.Port}#{Uri.EscapeDataString(profile.DisplayName)}";
    }

    private static string BuildUserPassLink(VmessProfile profile, string scheme)
    {
        var userInfo = string.IsNullOrWhiteSpace(profile.UserId)
            ? ""
            : $"{Uri.EscapeDataString(profile.UserId)}:{Uri.EscapeDataString(profile.Password)}@";
        return $"{scheme}://{userInfo}{profile.Address}:{profile.Port}#{Uri.EscapeDataString(profile.DisplayName)}";
    }

    private static string BuildQuery(IEnumerable<(string Key, string Value)> values)
    {
        var items = values
            .Where(item => !string.IsNullOrWhiteSpace(item.Value))
            .Select(item => $"{Uri.EscapeDataString(item.Key)}={Uri.EscapeDataString(item.Value)}")
            .ToArray();
        return items.Length == 0 ? "" : $"?{string.Join("&", items)}";
    }

    private static string GetConfigDirectory()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NaiwaProxy");
    }

    private static string GetCurrentVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString(3)
            ?? "未知";
        return version.Split('+')[0];
    }

    private static string GetBuildTime()
    {
        var executablePath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(executablePath) && File.Exists(executablePath))
        {
            return File.GetLastWriteTime(executablePath).ToString("yyyy-MM-dd HH:mm");
        }

        return DateTime.Now.ToString("yyyy-MM-dd HH:mm");
    }

    private static string GetExecutableVersion(string executablePath, string arguments)
    {
        if (!File.Exists(executablePath))
        {
            return "缺失";
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = arguments,
                WorkingDirectory = Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });

            if (process is null)
            {
                return "无法读取";
            }

            var output = process.StandardOutput.ReadLine();
            process.WaitForExit(3000);
            return string.IsNullOrWhiteSpace(output) ? "无法读取" : ShortenExecutableVersion(output.Trim());
        }
        catch
        {
            return "无法读取";
        }
    }

    private static string ShortenExecutableVersion(string output)
    {
        var parts = output.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length >= 2 && string.Equals(parts[0], "Xray", StringComparison.OrdinalIgnoreCase))
        {
            return $"Xray {parts[1]}";
        }

        return output;
    }

    private static int CompareVersionText(string left, string right)
    {
        var leftParts = ExtractVersionParts(left);
        var rightParts = ExtractVersionParts(right);
        for (var i = 0; i < 4; i++)
        {
            var comparison = leftParts[i].CompareTo(rightParts[i]);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return 0;
    }

    private static int[] ExtractVersionParts(string value)
    {
        var match = Regex.Match(value, @"\d+(?:\.\d+){0,3}");
        if (!match.Success)
        {
            return [0, 0, 0, 0];
        }

        var parts = match.Value.Split('.');
        var result = new[] { 0, 0, 0, 0 };
        for (var i = 0; i < parts.Length && i < result.Length; i++)
        {
            _ = int.TryParse(parts[i], out result[i]);
        }

        return result;
    }

    private static void OpenPath(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "NaiwaProxy", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static void ShowError(Exception exception)
    {
        MessageBox.Show(exception.Message, "NaiwaProxy", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
