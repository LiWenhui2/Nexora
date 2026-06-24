using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
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
using ZXing;
using ZXing.Common;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;
using Application = System.Windows.Application;
using Clipboard = System.Windows.Clipboard;
using Color = System.Windows.Media.Color;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace NaiwaProxy;

public partial class MainWindow : Window
{
    private const double AboutTwoColumnBreakpoint = 980;
    private static readonly DateTime AppStartTime = DateTime.Now;
    private const string ProjectUrl = "https://github.com/LiWenhui2/NaiwaProxy";
    private const string LatestReleaseApi = "https://api.github.com/repos/LiWenhui2/NaiwaProxy/releases/latest";
    private readonly SettingsStore _settingsStore = new();
    private readonly CoreService _coreService = new();
    private readonly AuthService _authService = new();
    private readonly ObservableCollection<VmessProfile> _profiles = [];
    private readonly ObservableCollection<WebsiteTestItem> _websiteTests = [];
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
    private bool _suppressNodePickerComboEvent;
    private bool _suppressRegionFilterComboEvent;
    private bool _suppressRunAtStartupToggleEvent;
    private bool _suppressRunAtStartupSilentToggleEvent;
    private bool _suppressAllowLanAccessToggleEvent;
    private bool _startSilent;
    private bool _isUiReady;
    private bool _isExiting;
    private Forms.NotifyIcon? _trayIcon;
    private Forms.ContextMenuStrip? _trayMenu;
    private string _lastDownSpeedText = "-";
    private string _lastUpSpeedText = "-";
    private CancellationTokenSource? _regionEnrichmentCancellation;
    private CancellationTokenSource? _websiteTestCancellation;
    private readonly DispatcherTimer _registerCodeCooldownTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _aboutRuntimeTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Dictionary<string, DispatcherTimer> _subscriptionRefreshTimers = new(StringComparer.OrdinalIgnoreCase);
    private int _registerCodeCooldownSeconds;
    private string? _subscriptionContextMenuName;

    public MainWindow(bool startSilent = false)
    {
        _startSilent = startSilent;
        DiagnosticLogService.Startup("MainWindow constructor begin");
        InitializeComponent();
        RefreshLogView();
        LoadBrandIcon();
        LoadInfoHintIcons();
        InitializeTray();
        _trafficTimer.Tick += TrafficTimer_Tick;
        _coreService.CoreExited += CoreService_CoreExited;
        _authService.AuthStateChanged += AuthService_AuthStateChanged;
        _registerCodeCooldownTimer.Tick += RegisterCodeCooldownTimer_Tick;
        _aboutRuntimeTimer.Tick += AboutRuntimeTimer_Tick;
        _profilesView = CollectionViewSource.GetDefaultView(_profiles);
        _profilesView.Filter = FilterProfile;
        _profilesView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(VmessProfile.SubscriptionDisplay)));
        ProfilesGrid.ItemsSource = _profilesView;
        InitializeWebsiteTests();
        _isUiReady = true;
        LoadSettings();
        Loaded += MainWindow_Loaded;
        SizeChanged += MainWindow_SizeChanged;
        DiagnosticLogService.EntryAdded += DiagnosticLogService_EntryAdded;
        DiagnosticLogService.Startup("MainWindow constructor complete");
        Closing += MainWindow_Closing;
        Closed += (_, _) =>
        {
            DiagnosticLogService.EntryAdded -= DiagnosticLogService_EntryAdded;
            _authService.AuthStateChanged -= AuthService_AuthStateChanged;
            _registerCodeCooldownTimer.Stop();
            _registerCodeCooldownTimer.Tick -= RegisterCodeCooldownTimer_Tick;
            _aboutRuntimeTimer.Stop();
            _aboutRuntimeTimer.Tick -= AboutRuntimeTimer_Tick;
            DisposeTray();
            _regionEnrichmentCancellation?.Cancel();
            _regionEnrichmentCancellation?.Dispose();
            _latencyTestCancellation?.Cancel();
            _latencyTestCancellation?.Dispose();
            _websiteTestCancellation?.Cancel();
            _websiteTestCancellation?.Dispose();
            _trafficTimer.Stop();
            _settingsStore.Save(_settings);
            TunService.Stop();
            _coreService.Stop(_settings);
            ApplySystemProxyMode("Clear", save: false);
        };
    }

    private void LoadBrandIcon()
    {
        var bitmap = TryLoadAppBitmap("assets/app-icon.png");
        if (bitmap is null)
        {
            return;
        }

        BrandIconImage.Source = bitmap;
        LoadWindowIcon();
    }

    private void LoadInfoHintIcons()
    {
        var bitmap = TryLoadAppBitmap("assets/about-info.png");
        if (bitmap is null)
        {
            return;
        }

        RunAtStartupSilentInfoIcon.Source = bitmap;
        AllowLanAccessInfoIcon.Source = bitmap;
    }

    private void LoadWindowIcon()
    {
        try
        {
            using var stream = OpenAppResourceStream("assets/app-icon.ico");
            if (stream is not null)
            {
                Icon = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                return;
            }

            var iconPath = Path.Combine(AppContext.BaseDirectory, "assets", "app-icon.ico");
            if (!File.Exists(iconPath))
            {
                return;
            }

            using var fileStream = File.OpenRead(iconPath);
            Icon = BitmapFrame.Create(fileStream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        }
        catch
        {
            // Fall back to the executable icon if the bundled icon cannot be decoded.
        }
    }

    private static BitmapImage? TryLoadAppBitmap(string resourcePath)
    {
        try
        {
            using var stream = OpenAppResourceStream(resourcePath);
            if (stream is not null)
            {
                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                memory.Position = 0;
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = memory;
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
        }
        catch
        {
        }

        var filePath = Path.Combine(AppContext.BaseDirectory, resourcePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(filePath))
        {
            return null;
        }

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(filePath, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private static Stream? OpenAppResourceStream(string resourcePath)
    {
        var packUri = new Uri($"pack://application:,,,/{resourcePath}", UriKind.Absolute);
        return Application.GetResourceStream(packUri)?.Stream;
    }

    private static Drawing.Icon? TryLoadTrayIcon()
    {
        try
        {
            using var stream = OpenAppResourceStream("assets/app-icon.ico");
            if (stream is not null)
            {
                return new Drawing.Icon(stream);
            }
        }
        catch
        {
        }

        var iconPath = Path.Combine(AppContext.BaseDirectory, "assets", "app-icon.ico");
        if (File.Exists(iconPath))
        {
            try
            {
                return new Drawing.Icon(iconPath);
            }
            catch
            {
            }
        }

        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath) && File.Exists(processPath))
        {
            return Drawing.Icon.ExtractAssociatedIcon(processPath);
        }

        return Drawing.SystemIcons.Application;
    }

    private void InitializeTray()
    {
        _trayMenu = new Forms.ContextMenuStrip();
        _trayMenu.Opening += (_, _) => RebuildTrayMenu();

        var icon = TryLoadTrayIcon() ?? Drawing.SystemIcons.Application;

        _trayIcon = new Forms.NotifyIcon
        {
            Icon = icon,
            Visible = true,
            Text = "Nexora",
            ContextMenuStrip = _trayMenu
        };
        _trayIcon.DoubleClick += (_, _) => ShowMainWindow();
        UpdateTrayStatus();
    }

    private void RebuildTrayMenu()
    {
        if (_trayMenu is null)
        {
            return;
        }

        _trayMenu.Items.Clear();
        var active = GetCurrentProfileOrNull();
        AddTrayStatusItem($"当前节点：{active?.DisplayName ?? "-"}");
        AddTrayStatusItem($"运行状态：{(_coreService.IsRunning ? "运行中" : "已停止")}");
        AddTrayStatusItem($"系统代理：{FormatSystemProxyMode(_settings.SystemProxyMode)}");
        AddTrayStatusItem($"上传下载：↓ {_lastDownSpeedText} / ↑ {_lastUpSpeedText}");
        _trayMenu.Items.Add(new Forms.ToolStripSeparator());

        AddTrayMenuItem("启动代理", async () => await StartProxyFromTrayAsync(), !_coreService.IsRunning && _profiles.Count > 0);
        AddTrayMenuItem("停止代理", StopProxyFromTray, _coreService.IsRunning);
        AddTrayMenuItem("开启系统代理", EnableSystemProxyFromTray, _settings.SystemProxyMode != "Auto");
        AddTrayMenuItem("关闭系统代理", DisableSystemProxyFromTray, _settings.SystemProxyMode != "Clear");

        var nodeMenu = new Forms.ToolStripMenuItem("切换节点") { Enabled = _profiles.Count > 0 };
        foreach (var profile in _profiles)
        {
            var item = new Forms.ToolStripMenuItem(profile.PickerDisplay)
            {
                Checked = profile.Id == _settings.SelectedProfileId
            };
            item.Click += async (_, _) => await Dispatcher.InvokeAsync(async () => await SwitchToProfileAsync(profile));
            nodeMenu.DropDownItems.Add(item);
        }

        _trayMenu.Items.Add(nodeMenu);

        var routingMenu = new Forms.ToolStripMenuItem("切换代理模式");
        foreach (var mode in new[] { "Global", "BypassChina", "BypassLan", "Direct", "Custom" })
        {
            var item = new Forms.ToolStripMenuItem(FormatRoutingMode(mode))
            {
                Checked = _settings.RoutingMode == mode
            };
            item.Click += async (_, _) => await Dispatcher.InvokeAsync(async () => await SwitchRoutingModeAsync(mode));
            routingMenu.DropDownItems.Add(item);
        }

        _trayMenu.Items.Add(routingMenu);
        _trayMenu.Items.Add(new Forms.ToolStripSeparator());
        AddTrayMenuItem("打开主窗口", ShowMainWindow);
        AddTrayMenuItem("查看日志", ShowLogPageFromTray);
        AddTrayMenuItem("退出程序", ExitApplication);
    }

    private void AddTrayStatusItem(string text)
    {
        _trayMenu?.Items.Add(new Forms.ToolStripMenuItem(text) { Enabled = false });
    }

    private void AddTrayMenuItem(string text, Action action, bool enabled = true)
    {
        var item = new Forms.ToolStripMenuItem(text) { Enabled = enabled };
        item.Click += (_, _) => Dispatcher.Invoke(action);
        _trayMenu?.Items.Add(item);
    }

    private void UpdateTrayStatus()
    {
        if (_trayIcon is null)
        {
            return;
        }

        var active = GetCurrentProfileOrNull();
        var status = _coreService.IsRunning ? "运行中" : "已停止";
        var text = $"Nexora | {status} | {active?.DisplayName ?? "无节点"}";
        _trayIcon.Text = text.Length > 63 ? string.Concat(text.AsSpan(0, 60), "...") : text;
    }

    private void DisposeTray()
    {
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        _trayMenu?.Dispose();
        _trayMenu = null;
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_isExiting)
        {
            return;
        }

        e.Cancel = true;
        Hide();
        ShowInTaskbar = false;
        UpdateTrayStatus();
    }

    private void ShowMainWindow()
    {
        ShowInTaskbar = true;
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    private async Task StartProxyFromTrayAsync()
    {
        await StartProxyAsync();
        SyncProxyToggleFromCoreState();
    }

    private void StopProxyFromTray()
    {
        StopProxy();
        SyncProxyToggleFromCoreState();
    }

    private void EnableSystemProxyFromTray()
    {
        SelectSystemProxyCombo("Auto");
        ApplySystemProxyMode("Auto", save: true);
    }

    private void DisableSystemProxyFromTray()
    {
        SelectSystemProxyCombo("Clear");
        ApplySystemProxyMode("Clear", save: true);
    }

    private async Task SwitchToProfileAsync(VmessProfile profile)
    {
        SaveProfiles(profile.Id);
        ProfilesGrid.SelectedItem = profile;
        UpdateNodeStatusBar(profile);
        if (_coreService.IsRunning)
        {
            await RestartCoreAsync();
        }

        UpdateTrayStatus();
    }

    private async Task SwitchRoutingModeAsync(string mode)
    {
        _settings.RoutingMode = mode;
        _settingsStore.Save(_settings);
        SelectRoutingCombo(mode);
        UpdateRoutingEditorVisibility();
        if (_coreService.IsRunning)
        {
            await RestartCoreAsync();
        }

        UpdateTrayStatus();
    }

    private void ExitApplication()
    {
        _isExiting = true;
        Close();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_Loaded;

        if (_profiles.Count > 0)
        {
            _ = RunStartupLatencyTestsAsync();
        }

        if (_startSilent)
        {
            Hide();
            ShowInTaskbar = false;
            UpdateTrayStatus();
        }

        if (_profiles.Count == 0)
        {
            return;
        }

        try
        {
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);

            if (!_coreService.IsRunning)
            {
                await StartProxyAsync();
                SyncProxyToggleFromCoreState();
            }
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private async Task RunStartupLatencyTestsAsync()
    {
        if (_profiles.Count == 0)
        {
            return;
        }

        DiagnosticLogService.Info($"Startup latency test started for {_profiles.Count} profiles.");
        await RunTcpLatencyTestsAsync(_profiles.ToList(), parallel: true);
        DiagnosticLogService.Info("Startup latency test completed.");
    }

    private void LoadSettings()
    {
        _settings = _settingsStore.Load();
        var metadataChanged = false;
        foreach (var profile in _settings.Profiles)
        {
            if (ProfileMetadataHelper.Ensure(profile))
            {
                metadataChanged = true;
            }
        }

        if (metadataChanged)
        {
            _settingsStore.Save(_settings);
        }

        _profiles.Clear();
        foreach (var profile in _settings.Profiles)
        {
            _profiles.Add(profile);
        }

        NodePickerCombo.ItemsSource = _profiles;
        SelectSystemProxyCombo(_settings.SystemProxyMode);
        SelectRoutingCombo(_settings.RoutingMode);
        UpdateRoutingEditorVisibility();

        var selected = GetSelectedProfileOrNull();
        if (!string.IsNullOrWhiteSpace(_settings.SelectedProfileId) && selected is null)
        {
            _settings.SelectedProfileId = null;
            _settingsStore.Save(_settings);
        }

        ProfilesGrid.SelectedItem = selected;
        SyncNodePickerDisplay(selected);
        SyncTunToggleFromSettings();
        SyncProxyToggleFromCoreState();
        UpdateActiveProfileMarkers(_settings.SelectedProfileId);
        UpdateNodeStatusBar(selected);
        ConfigureAuthService();
        UpdateAuthSidebar();
        UpdateSidebarStatus();
        UpdateTrafficStatsDisplay();
        RefreshRegionFilterOptions();
        RefreshSubscriptionFilterOptions();
        ScheduleRegionEnrichment();
        SyncRunAtStartupFromSettings();
        SyncAllowLanAccessFromSettings();
        ApplyStartupSettings(save: false);
        RestoreSubscriptionAutoRefreshTimers();
    }

    private void InitializeWebsiteTests()
    {
        if (_websiteTests.Count > 0)
        {
            return;
        }

        foreach (var target in WebsiteConnectivityTestService.DefaultTargets)
        {
            _websiteTests.Add(new WebsiteTestItem(target.Name, target.Url, target.IconFileName));
        }

        WebsiteTestList.ItemsSource = _websiteTests;
    }

    private void UpdateNodeTestHeader()
    {
        if (!_isUiReady || NodeTestProfileText is null || NodeTestProxyStateText is null)
        {
            return;
        }

        var profile = GetCurrentProfileOrNull();
        NodeTestProfileText.Text = profile?.DisplayName ?? "无节点";
        if (_coreService.IsRunning)
        {
            NodeTestProxyStateText.Text = "运行中";
            NodeTestProxyStateText.Foreground = GreenBrush();
        }
        else
        {
            NodeTestProxyStateText.Text = "未运行";
            NodeTestProxyStateText.Foreground = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B));
        }
    }

    private void SetWebsiteTestButtonsEnabled(bool enabled)
    {
        if (!_isUiReady)
        {
            return;
        }

        RunAllWebsiteTestsButton.IsEnabled = enabled;
        ResetWebsiteTestsButton.IsEnabled = enabled;
    }

    private async Task RunWebsiteTestAsync(WebsiteTestItem item)
    {
        if (!_coreService.IsRunning)
        {
            MessageBox.Show("请先启用代理后再进行网站连通性测试。", "Nexora", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        item.BeginTest();
        var result = await WebsiteConnectivityTestService.TestAsync(item.Url, _settings.HttpPort);
        if (result.Success && result.LatencyMs is not null)
        {
            item.CompleteSuccess(result.LatencyMs.Value);
            return;
        }

        item.CompleteFailure(result.ErrorMessage);
    }

    private async Task RunAllWebsiteTestsAsync()
    {
        if (!_coreService.IsRunning)
        {
            MessageBox.Show("请先启用代理后再进行网站连通性测试。", "Nexora", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _websiteTestCancellation?.Cancel();
        _websiteTestCancellation?.Dispose();
        _websiteTestCancellation = new CancellationTokenSource();
        var cancellationToken = _websiteTestCancellation.Token;

        SetWebsiteTestButtonsEnabled(false);
        try
        {
            foreach (var item in _websiteTests)
            {
                cancellationToken.ThrowIfCancellationRequested();
                item.BeginTest();
            }

            await Task.WhenAll(_websiteTests.Select(async item =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await WebsiteConnectivityTestService.TestAsync(item.Url, _settings.HttpPort, cancellationToken: cancellationToken);
                await Dispatcher.InvokeAsync(() =>
                {
                    if (result.Success && result.LatencyMs is not null)
                    {
                        item.CompleteSuccess(result.LatencyMs.Value);
                    }
                    else
                    {
                        item.CompleteFailure(result.ErrorMessage);
                    }
                });
            }));
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
            SetWebsiteTestButtonsEnabled(true);
        }
    }

    private void NodeTestNavButton_Click(object sender, RoutedEventArgs e) => ShowNodeTestPage();

    private void ShowNodeTestPage()
    {
        UpdateNodeTestHeader();
        ShowPage(NodeTestPageScroll, NodeTestNavButton);
    }

    private async void RunAllWebsiteTestsButton_Click(object sender, RoutedEventArgs e)
    {
        await RunAllWebsiteTestsAsync();
    }

    private async void WebsiteTestItemButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: WebsiteTestItem item })
        {
            return;
        }

        SetWebsiteTestButtonsEnabled(false);
        try
        {
            await RunWebsiteTestAsync(item);
        }
        finally
        {
            SetWebsiteTestButtonsEnabled(true);
        }
    }

    private void ResetWebsiteTestsButton_Click(object sender, RoutedEventArgs e)
    {
        _websiteTestCancellation?.Cancel();
        foreach (var item in _websiteTests)
        {
            item.Reset();
        }
    }

    private void SyncRunAtStartupFromSettings()
    {
        if (!_isUiReady || RunAtStartupToggle is null || RunAtStartupSilentToggle is null)
        {
            return;
        }

        _suppressRunAtStartupToggleEvent = true;
        _suppressRunAtStartupSilentToggleEvent = true;
        RunAtStartupToggle.IsChecked = _settings.RunAtStartup;
        RunAtStartupSilentToggle.IsChecked = _settings.RunAtStartupSilent;
        _suppressRunAtStartupToggleEvent = false;
        _suppressRunAtStartupSilentToggleEvent = false;
    }

    private void ApplyStartupSettings(bool save)
    {
        try
        {
            StartupService.SetStartup(_settings.RunAtStartup, _settings.RunAtStartupSilent);
            if (save)
            {
                _settingsStore.Save(_settings);
            }
        }
        catch (Exception ex)
        {
            _settings.RunAtStartup = StartupService.IsEnabled();
            _settings.RunAtStartupSilent = StartupService.IsSilentEnabled();
            SyncRunAtStartupFromSettings();
            ShowError(ex);
        }
    }

    private void RunAtStartupToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!_isUiReady || _suppressRunAtStartupToggleEvent)
        {
            return;
        }

        _settings.RunAtStartup = RunAtStartupToggle.IsChecked == true;
        if (!_settings.RunAtStartup)
        {
            _settings.RunAtStartupSilent = false;
            _suppressRunAtStartupSilentToggleEvent = true;
            RunAtStartupSilentToggle.IsChecked = false;
            _suppressRunAtStartupSilentToggleEvent = false;
        }

        ApplyStartupSettings(save: true);
    }

    private void RunAtStartupSilentToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!_isUiReady || _suppressRunAtStartupSilentToggleEvent)
        {
            return;
        }

        _settings.RunAtStartupSilent = RunAtStartupSilentToggle.IsChecked == true;
        if (_settings.RunAtStartupSilent)
        {
            _settings.RunAtStartup = true;
            _suppressRunAtStartupToggleEvent = true;
            RunAtStartupToggle.IsChecked = true;
            _suppressRunAtStartupToggleEvent = false;
        }

        ApplyStartupSettings(save: true);
    }

    private void SyncAllowLanAccessFromSettings()
    {
        if (!_isUiReady || AllowLanAccessToggle is null)
        {
            return;
        }

        _suppressAllowLanAccessToggleEvent = true;
        AllowLanAccessToggle.IsChecked = _settings.AllowLanAccess;
        _suppressAllowLanAccessToggleEvent = false;
    }

    private async void AllowLanAccessToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!_isUiReady || _suppressAllowLanAccessToggleEvent)
        {
            return;
        }

        _settings.AllowLanAccess = AllowLanAccessToggle.IsChecked == true;
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

    private void RefreshRegionFilterOptions()
    {
        if (!_isUiReady || RegionFilterCombo is null)
        {
            return;
        }

        var selected = NormalizeFilterValue((RegionFilterCombo.SelectedItem as ComboBoxItem)?.Content?.ToString(), "地区");
        _suppressRegionFilterComboEvent = true;
        RegionFilterCombo.Items.Clear();
        RegionFilterCombo.Items.Add(new ComboBoxItem { Content = "地区：全部" });

        foreach (var region in _profiles
                     .Select(p => p.RegionCountryDisplay)
                     .Where(r => !string.IsNullOrWhiteSpace(r) && r != "-")
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(r => r, StringComparer.OrdinalIgnoreCase))
        {
            RegionFilterCombo.Items.Add(new ComboBoxItem { Content = $"地区：{region}" });
        }

        var matched = RegionFilterCombo.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(NormalizeFilterValue(item.Content?.ToString(), "地区"), selected, StringComparison.OrdinalIgnoreCase));
        RegionFilterCombo.SelectedItem = matched ?? RegionFilterCombo.Items[0];
        _suppressRegionFilterComboEvent = false;
    }

    private void RefreshSubscriptionFilterOptions()
    {
        if (!_isUiReady || SubscriptionFilterCombo is null)
        {
            return;
        }

        var selected = NormalizeFilterValue((SubscriptionFilterCombo.SelectedItem as ComboBoxItem)?.Content?.ToString(), "来源");
        SubscriptionFilterCombo.Items.Clear();
        SubscriptionFilterCombo.Items.Add(new ComboBoxItem { Content = "来源：全部订阅" });

        foreach (var subscription in _profiles
                     .Select(p => p.SubscriptionDisplay)
                     .Where(s => !string.IsNullOrWhiteSpace(s))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
        {
            SubscriptionFilterCombo.Items.Add(new ComboBoxItem { Content = $"来源：{subscription}" });
        }

        var matched = SubscriptionFilterCombo.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(NormalizeFilterValue(item.Content?.ToString(), "来源"), selected, StringComparison.OrdinalIgnoreCase));
        SubscriptionFilterCombo.SelectedItem = matched ?? SubscriptionFilterCombo.Items[0];
    }

    private static string NormalizeFilterValue(string? value, string prefix)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        var text = value.Trim();
        var fullPrefix = $"{prefix}：";
        return text.StartsWith(fullPrefix, StringComparison.Ordinal)
            ? text[fullPrefix.Length..].Trim()
            : text;
    }

    private void ScheduleRegionEnrichment(IEnumerable<VmessProfile>? profiles = null)
    {
        _regionEnrichmentCancellation?.Cancel();
        _regionEnrichmentCancellation?.Dispose();
        _regionEnrichmentCancellation = new CancellationTokenSource();
        var token = _regionEnrichmentCancellation.Token;
        var targets = profiles?.ToList() ?? _profiles.ToList();

        _ = Task.Run(async () =>
        {
            try
            {
                var updated = await RegionEnrichmentService.EnrichRegionsAsync(targets, token);
                if (updated == 0 || token.IsCancellationRequested)
                {
                    return;
                }

                await Dispatcher.InvokeAsync(() =>
                {
                    RefreshRegionFilterOptions();
                    RefreshSubscriptionFilterOptions();
                    ProfilesGrid.Items.Refresh();
                    _settings.Profiles = _profiles.ToList();
                    _settingsStore.Save(_settings);
                });
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
            }
        }, token);
    }

    private bool FilterProfile(object item)
    {
        if (item is not VmessProfile profile)
        {
            return false;
        }

        var protocol = NormalizeFilterValue((ProtocolFilterCombo?.SelectedItem as ComboBoxItem)?.Content?.ToString(), "协议");
        if (!string.IsNullOrWhiteSpace(protocol) &&
            protocol != "全部" &&
            !string.Equals(profile.ProtocolDisplay, protocol, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var region = NormalizeFilterValue((RegionFilterCombo?.SelectedItem as ComboBoxItem)?.Content?.ToString(), "地区");
        if (!string.IsNullOrWhiteSpace(region) &&
            region != "全部" &&
            !string.Equals(profile.RegionCountryDisplay, region, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var latency = NormalizeFilterValue((LatencyFilterCombo?.SelectedItem as ComboBoxItem)?.Content?.ToString(), "延迟");
        if (latency == "可用" && profile.TcpLatencyMs is null)
        {
            return false;
        }

        if (latency == "超时" && profile.TcpLatencyDisplay != "Timeout")
        {
            return false;
        }

        if (AvailableOnlyCheck?.IsChecked == true && profile.TcpLatencyMs is null)
        {
            return false;
        }

        var subscription = NormalizeFilterValue((SubscriptionFilterCombo?.SelectedItem as ComboBoxItem)?.Content?.ToString(), "来源");
        if (!string.IsNullOrWhiteSpace(subscription) &&
            subscription != "全部订阅" &&
            !string.Equals(profile.SubscriptionDisplay, subscription, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private void FilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isUiReady || (sender == RegionFilterCombo && _suppressRegionFilterComboEvent))
        {
            return;
        }

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
        var sort = NormalizeFilterValue((SortCombo?.SelectedItem as ComboBoxItem)?.Content?.ToString(), "排序");
        if (sort == "延迟优先")
        {
            _profilesView.SortDescriptions.Add(new SortDescription(nameof(VmessProfile.TcpLatencyMs), ListSortDirection.Ascending));
            _profilesView.SortDescriptions.Add(new SortDescription(nameof(VmessProfile.DisplayName), ListSortDirection.Ascending));
        }
    }

    private void ProfilesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Selection is only for list operations. The header always shows the active node.
    }

    private void NodePickerCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isUiReady || _suppressNodePickerComboEvent || NodePickerCombo.SelectedItem is not VmessProfile profile)
        {
            return;
        }

        _settings.SelectedProfileId = profile.Id;
        SaveProfiles(profile.Id);
        UpdateNodeStatusBar(profile);
        if (_coreService.IsRunning)
        {
            _ = RestartCoreAsync();
        }
    }

    private void UpdateNodeStatusBar(VmessProfile? profile)
    {
        if (profile is null)
        {
            NodeAddressText.Text = "[VMess] -";
            CurrentTcpLatencyText.Text = "-";
            NodeAvailabilityText.Text = "无节点";
            return;
        }

        NodeAddressText.Text = $"[{profile.ProtocolDisplay}] {profile.DisplayName} · {profile.Endpoint}";
        CurrentTcpLatencyText.Text = profile.TcpLatencyDisplay;
        NodeAvailabilityText.Text = profile.TcpLatencyDisplay switch
        {
            "Timeout" => "超时",
            "-" or "..." => "待测速",
            _ when profile.TcpLatencyMs is not null => profile.StatusDisplay,
            _ => "待测速"
        };

        var tagBackground = NodeAvailabilityText.Text switch
        {
            "可用" => "#DCFCE7",
            "当前" => "#DBEAFE",
            "超时" => "#FEE2E2",
            "过期" => "#FEE2E2",
            _ => "#FEF3C7"
        };
        var tagBorder = NodeAvailabilityText.Text switch
        {
            "可用" => "#86EFAC",
            "当前" => "#93C5FD",
            "超时" => "#FCA5A5",
            "过期" => "#FCA5A5",
            _ => "#FDE68A"
        };
        var tagForeground = NodeAvailabilityText.Text switch
        {
            "可用" => "#166534",
            "当前" => "#1D4ED8",
            "超时" => "#991B1B",
            "过期" => "#991B1B",
            _ => "#92400E"
        };
        NodeAvailabilityTag.Background = (SolidColorBrush)new BrushConverter().ConvertFromString(tagBackground)!;
        NodeAvailabilityTag.BorderBrush = (SolidColorBrush)new BrushConverter().ConvertFromString(tagBorder)!;
        NodeAvailabilityText.Foreground = (SolidColorBrush)new BrushConverter().ConvertFromString(tagForeground)!;
    }

    private void UpdateActiveProfileMarkers(string? activeId)
    {
        foreach (var profile in _profiles)
        {
            profile.SetActive(!string.IsNullOrWhiteSpace(activeId) && profile.Id == activeId);
        }

        ProfilesGrid.Items.Refresh();

        var active = string.IsNullOrWhiteSpace(activeId)
            ? null
            : _profiles.FirstOrDefault(p => p.Id == activeId);

        SideNodeText.Text = active is null
            ? "节点：无活动节点"
            : $"节点：{active.DisplayName} · {active.ProtocolDisplay}";

        SyncNodePickerDisplay(active);
        UpdateNodeStatusBar(active);
        UpdateTrayStatus();
    }

    private void SyncNodePickerDisplay(VmessProfile? active = null)
    {
        if (!_isUiReady || NodePickerCombo is null)
        {
            return;
        }

        active ??= GetSelectedProfileOrNull();
        _suppressNodePickerComboEvent = true;
        NodePickerCombo.SelectedItem = active;
        NodePickerCombo.Text = active?.PickerDisplay ?? "无活动节点";
        _suppressNodePickerComboEvent = false;
    }

    private void UpdateSidebarStatus()
    {
        var running = _coreService.IsRunning;
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
        UpdateTrayStatus();
    }

    private void ConfigureAuthService()
    {
        var apiBaseUrl = string.IsNullOrWhiteSpace(_settings.AuthApiBaseUrl)
            ? "http://localhost:8080"
            : _settings.AuthApiBaseUrl;
        _authService.Configure(apiBaseUrl);
    }

    private void AuthService_AuthStateChanged()
    {
        Dispatcher.Invoke(UpdateAuthSidebar);
    }

    private void UpdateAuthSidebar()
    {
        if (!_isUiReady || AuthGuestPanel is null || AuthUserPanel is null)
        {
            return;
        }

        if (_authService.IsAuthenticated)
        {
            AuthGuestPanel.Visibility = Visibility.Collapsed;
            AuthUserPanel.Visibility = Visibility.Visible;
            SideAuthEmailText.Text = _authService.CurrentEmail ?? "";
        }
        else
        {
            AuthGuestPanel.Visibility = Visibility.Visible;
            AuthUserPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void SideLoginButton_Click(object sender, RoutedEventArgs e) => ShowLoginPage();

    private void SideLogoutButton_Click(object sender, RoutedEventArgs e)
    {
        _authService.Logout();
        ClearAuthMessages();
        MessageBox.Show("已退出登录。", "Nexora", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void GoToRegisterPageButton_Click(object sender, RoutedEventArgs e) => ShowRegisterPage();

    private void GoToLoginPageButton_Click(object sender, RoutedEventArgs e) => ShowLoginPage();

    private void ShowLoginPage()
    {
        ClearAuthMessages();
        AuthDialogOverlay.Visibility = Visibility.Visible;
        LoginPageScroll.Visibility = Visibility.Visible;
        RegisterPageScroll.Visibility = Visibility.Collapsed;
        LoginEmailBox.Focus();
    }

    private void ShowRegisterPage()
    {
        ClearAuthMessages();
        AuthDialogOverlay.Visibility = Visibility.Visible;
        LoginPageScroll.Visibility = Visibility.Collapsed;
        RegisterPageScroll.Visibility = Visibility.Visible;
        RegisterEmailBox.Focus();
    }

    private void CloseAuthDialog()
    {
        AuthDialogOverlay.Visibility = Visibility.Collapsed;
        LoginPageScroll.Visibility = Visibility.Collapsed;
        RegisterPageScroll.Visibility = Visibility.Collapsed;
    }

    private void CloseAuthDialogButton_Click(object sender, RoutedEventArgs e) => CloseAuthDialog();

    private void AuthDialogBackdrop_MouseDown(object sender, MouseButtonEventArgs e) => CloseAuthDialog();

    private void ClearAuthMessages()
    {
        LoginMessageText.Text = "";
        LoginMessageText.Visibility = Visibility.Collapsed;
        RegisterMessageText.Text = "";
        RegisterMessageText.Visibility = Visibility.Collapsed;
    }

    private void ShowAuthMessage(TextBlock target, string message, bool isSuccess = false)
    {
        target.Text = message;
        target.Foreground = isSuccess ? GreenBrush() : new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));
        target.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void LoginSubmitButton_Click(object sender, RoutedEventArgs e)
    {
        LoginSubmitButton.IsEnabled = false;
        LoginSubmitButton.Content = "登录中…";
        try
        {
            var result = await _authService.LoginAsync(LoginEmailBox.Text, LoginPasswordBox.Password);
            if (!result.Success)
            {
                ShowAuthMessage(LoginMessageText, result.Message);
                return;
            }

            LoginPasswordBox.Clear();
            ShowAuthMessage(LoginMessageText, result.Message, isSuccess: true);
            CloseAuthDialog();
            ShowNodePage();
        }
        finally
        {
            LoginSubmitButton.IsEnabled = true;
            LoginSubmitButton.Content = "登录";
        }
    }

    private async void SendRegisterCodeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_registerCodeCooldownSeconds > 0)
        {
            return;
        }

        SendRegisterCodeButton.IsEnabled = false;
        SendRegisterCodeButton.Content = "发送中…";
        try
        {
            var result = await _authService.SendRegisterCodeAsync(RegisterEmailBox.Text);
            if (!result.Success)
            {
                ShowAuthMessage(RegisterMessageText, result.Message);
                return;
            }

            ShowAuthMessage(RegisterMessageText, result.Message, isSuccess: true);
            StartRegisterCodeCooldown();
        }
        finally
        {
            if (_registerCodeCooldownSeconds <= 0)
            {
                SendRegisterCodeButton.IsEnabled = true;
                SendRegisterCodeButton.Content = "发送验证码";
            }
        }
    }

    private void StartRegisterCodeCooldown()
    {
        _registerCodeCooldownSeconds = 60;
        SendRegisterCodeButton.IsEnabled = false;
        SendRegisterCodeButton.Content = $"{_registerCodeCooldownSeconds}s";
        _registerCodeCooldownTimer.Start();
    }

    private void RegisterCodeCooldownTimer_Tick(object? sender, EventArgs e)
    {
        _registerCodeCooldownSeconds--;
        if (_registerCodeCooldownSeconds > 0)
        {
            SendRegisterCodeButton.Content = $"{_registerCodeCooldownSeconds}s";
            return;
        }

        _registerCodeCooldownTimer.Stop();
        SendRegisterCodeButton.IsEnabled = true;
        SendRegisterCodeButton.Content = "发送验证码";
    }

    private async void RegisterSubmitButton_Click(object sender, RoutedEventArgs e)
    {
        RegisterSubmitButton.IsEnabled = false;
        RegisterSubmitButton.Content = "注册中…";
        try
        {
            var result = await _authService.RegisterAsync(
                RegisterEmailBox.Text,
                RegisterPasswordBox.Password,
                RegisterConfirmPasswordBox.Password,
                RegisterCodeBox.Text);

            if (!result.Success)
            {
                ShowAuthMessage(RegisterMessageText, result.Message);
                return;
            }

            RegisterPasswordBox.Clear();
            RegisterConfirmPasswordBox.Clear();
            RegisterCodeBox.Clear();
            ShowAuthMessage(RegisterMessageText, result.Message, isSuccess: true);
            CloseAuthDialog();
            ShowNodePage();
        }
        finally
        {
            RegisterSubmitButton.IsEnabled = true;
            RegisterSubmitButton.Content = "注册";
        }
    }

    private void ReconcileProxyUiState()
    {
        _suppressProxyToggleEvent = true;
        ProxyToggle.IsChecked = _coreService.IsRunning;
        _suppressProxyToggleEvent = false;
        UpdateSidebarStatus();
    }

    private void CoreService_CoreExited(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            StopTrafficMonitor();
            _suppressProxyToggleEvent = true;
            ProxyToggle.IsChecked = false;
            _suppressProxyToggleEvent = false;
            UpdateSidebarStatus();
            DiagnosticLogService.Warning("Core process exited unexpectedly.");
        });
    }

    private void SyncProxyToggleFromCoreState()
    {
        ReconcileProxyUiState();
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

        try
        {
            TunService.Start(_settings);
        }
        catch (Exception ex)
        {
            _settings.IsTunEnabled = false;
            _settingsStore.Save(_settings);
            _suppressTunToggleEvent = true;
            TunToggle.IsChecked = false;
            _suppressTunToggleEvent = false;
            DiagnosticLogService.Error("TUN failed to start; TUN has been disabled automatically.", ex);
        }

        SyncTunToggleFromSettings();
    }

    private void StartTrafficMonitor()
    {
        _lastTrafficSnapshot = null;
        _lastTrafficSampleAt = DateTime.Now;
        _lastTrafficPersistAt = DateTime.Now;
        UpdateTrafficStatsDisplay(running: true);
        TrafficBadgeText.Text = "下行 0 B/s · 上传 0 B/s";
        _trafficTimer.Start();
        _ = RefreshTrafficAsync();
    }

    private void StopTrafficMonitor()
    {
        _trafficTimer.Stop();
        _lastTrafficSnapshot = null;
        UpdateTrafficStatsDisplay();
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
                EnsureTodayTraffic();
                _settings.TotalDownlinkBytes += downDelta;
                _settings.TotalUplinkBytes += upDelta;
                _settings.TodayDownlinkBytes += downDelta;
                _settings.TodayUplinkBytes += upDelta;
            }

            _lastTrafficSnapshot = snapshot;
            _lastTrafficSampleAt = now;

            UpdateTrafficStatsDisplay(downSpeed, upSpeed, running: true);
            TrafficBadgeText.Text = $"下行 {FormatBytes(downSpeed)}/s · 上传 {FormatBytes(upSpeed)}/s";

            if ((now - _lastTrafficPersistAt).TotalSeconds >= 5)
            {
                _settingsStore.Save(_settings);
                _lastTrafficPersistAt = now;
            }
        }
        catch
        {
            UpdateTrafficStatsDisplay(running: _coreService.IsRunning);
        }
        finally
        {
            _isRefreshingTraffic = false;
        }
    }

    private void EnsureTodayTraffic()
    {
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        if (_settings.TodayTrafficDate == today)
        {
            return;
        }

        _settings.TodayTrafficDate = today;
        _settings.TodayUplinkBytes = 0;
        _settings.TodayDownlinkBytes = 0;
    }

    private void UpdateTrafficStatsDisplay(double downSpeed = 0, double upSpeed = 0, bool running = false)
    {
        EnsureTodayTraffic();
        _lastDownSpeedText = running ? $"{FormatBytes(downSpeed)}/s" : "-";
        _lastUpSpeedText = running ? $"{FormatBytes(upSpeed)}/s" : "-";
        StatDownloadText.Text = running ? $"{FormatBytes(downSpeed)}/s" : "—";
        StatUploadText.Text = running ? $"{FormatBytes(upSpeed)}/s" : "—";
        StatTodayText.Text = FormatBytes(_settings.TodayUplinkBytes + _settings.TodayDownlinkBytes);
        StatTotalText.Text = FormatBytes(_settings.TotalDownlinkBytes + _settings.TotalUplinkBytes);
        UpdateTrayStatus();
    }

    private void ApplySubscriptionTrafficInfo(SubscriptionTrafficInfo? trafficInfo)
    {
        if (trafficInfo is null)
        {
            return;
        }

        _settings.SubscriptionUploadBytes = trafficInfo.UploadBytes;
        _settings.SubscriptionDownloadBytes = trafficInfo.DownloadBytes;
        _settings.SubscriptionTotalBytes = trafficInfo.TotalBytes;
        UpdateTrafficStatsDisplay(running: _coreService.IsRunning);
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
            _isExiting = true;
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
            var profile = GetActiveProfile();
            await _coreService.StartAsync(_settings, profile);
            SaveProfiles(profile.Id);
            EnsureSystemProxyForRunningCore();
            ApplySystemProxyMode(_settings.SystemProxyMode, save: true);
            StartTunIfEnabled();
            StartTrafficMonitor();
            SyncProxyToggleFromCoreState();
        }
        catch (Exception ex)
        {
            _coreService.Stop(_settings);
            StopTrafficMonitor();
            _suppressProxyToggleEvent = true;
            ProxyToggle.IsChecked = false;
            _suppressProxyToggleEvent = false;
            UpdateSidebarStatus();
            ShowError(ex);
        }

    }

    private void StopProxy()
    {
        _coreService.Stop(_settings);
        TunService.Stop();
        _settingsStore.Save(_settings);
        StopTrafficMonitor();
        if (_settings.SystemProxyMode is "Auto" or "Clear" or "Pac")
        {
            ApplySystemProxyMode("Clear", save: false);
        }

        UpdateSidebarStatus();
        SyncProxyToggleFromCoreState();
    }

    private async Task RestartCoreAsync()
    {
        if (!_coreService.IsRunning)
        {
            return;
        }

        var profile = GetActiveProfile();
        TunService.Stop();
        await _coreService.StartAsync(_settings, profile);
        EnsureSystemProxyForRunningCore();
        ApplySystemProxyMode(_settings.SystemProxyMode, save: true);
        StartTunIfEnabled();
        StartTrafficMonitor();
        UpdateSidebarStatus();
    }

    private void SystemProxyCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isUiReady || _suppressSystemProxyComboEvent)
        {
            return;
        }

        var mode = GetSystemProxyModeFromCombo();
        ApplySystemProxyMode(mode, save: true);
    }

    private HttpClient CreateProxiedHttpClient()
    {
        return new HttpClient(new HttpClientHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{_settings.HttpPort}"),
            UseProxy = true
        });
    }

    private HttpClient CreateUpdateHttpClient()
    {
        return _coreService.IsRunning
            ? CreateProxiedHttpClient()
            : new HttpClient();
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

    private void EnsureSystemProxyForRunningCore()
    {
        if (_settings.SystemProxyMode is "Clear")
        {
            _settings.SystemProxyMode = "Auto";
            SelectSystemProxyCombo("Auto");
            DiagnosticLogService.Info("System proxy mode was Clear; switched to Auto while starting core.");
        }
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
            "绕过大陆" => "BypassChina",
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
            "BypassChina" => "绕过大陆",
            "BypassLan" => "绕过局域网",
            "Direct" => "直连模式",
            "Custom" => "自定义规则",
            _ => "绕过大陆"
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

    private static SolidColorBrush GreenBrush() => new(Color.FromRgb(0x16, 0xA3, 0x4A));

    private void ProfilesGrid_PreviewMouseRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var row = ItemsControl.ContainerFromElement(ProfilesGrid, e.OriginalSource as DependencyObject) as DataGridRow;
        if (row?.Item is VmessProfile profile && !row.IsSelected)
        {
            ProfilesGrid.SelectedItems.Clear();
            ProfilesGrid.SelectedItem = profile;
        }
    }

    private void ProfilesGrid_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control)
        {
            ProfilesGrid.SelectAll();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Delete)
        {
            DeleteProfiles(GetSelectedProfiles());
            e.Handled = true;
        }
    }

    private void ProfilesGrid_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (CanProfilesGridScroll(e.Delta))
        {
            return;
        }

        ForwardNodePageMouseWheel(e);
    }

    private void NodePageChild_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        ForwardNodePageMouseWheel(e);
    }

    private void NodePageScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (IsDescendantOf(ProfilesGrid, e.OriginalSource as DependencyObject) && CanProfilesGridScroll(e.Delta))
        {
            return;
        }

        ForwardNodePageMouseWheel(e);
    }

    private bool CanProfilesGridScroll(int delta)
    {
        var scrollViewer = FindVisualChild<ScrollViewer>(ProfilesGrid);
        if (scrollViewer is null)
        {
            return false;
        }

        if (delta < 0)
        {
            return scrollViewer.VerticalOffset < scrollViewer.ScrollableHeight;
        }

        return scrollViewer.VerticalOffset > 0;
    }

    private static bool IsDescendantOf(DependencyObject ancestor, DependencyObject? node)
    {
        while (node is not null)
        {
            if (node == ancestor)
            {
                return true;
            }

            node = VisualTreeHelper.GetParent(node);
        }

        return false;
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
            {
                return match;
            }

            var nested = FindVisualChild<T>(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private void ForwardNodePageMouseWheel(MouseWheelEventArgs e)
    {
        if (NodePageScroll.Visibility != Visibility.Visible)
        {
            return;
        }

        e.Handled = true;
        NodePageScroll.ScrollToVerticalOffset(NodePageScroll.VerticalOffset - e.Delta);
    }

    private async void CtxSetActiveNode_Click(object sender, RoutedEventArgs e)
    {
        if (ProfilesGrid.SelectedItem is not VmessProfile profile)
        {
            return;
        }

        SaveProfiles(profile.Id);
        UpdateNodeStatusBar(profile);
        if (_coreService.IsRunning)
        {
            await RestartCoreAsync();
        }
    }

    private void CtxEditNode_Click(object sender, RoutedEventArgs e)
    {
        OpenEditDialog(ProfilesGrid.SelectedItem as VmessProfile);
    }

    private void CtxDeleteNode_Click(object sender, RoutedEventArgs e)
    {
        DeleteProfiles(GetSelectedProfiles());
    }

    private List<VmessProfile> GetSelectedProfiles() =>
        ProfilesGrid.SelectedItems.Cast<object>().OfType<VmessProfile>().ToList();

    private void DeleteProfiles(IReadOnlyList<VmessProfile> profilesToDelete)
    {
        if (profilesToDelete.Count == 0)
        {
            return;
        }

        var message = profilesToDelete.Count == 1
            ? $"确定删除节点「{profilesToDelete[0].DisplayName}」？"
            : $"确定删除选中的 {profilesToDelete.Count} 个节点？";

        if (MessageBox.Show(message, "Nexora", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        foreach (var profile in profilesToDelete)
        {
            _profiles.Remove(profile);
        }

        var nextActiveId = _profiles.FirstOrDefault(p => p.Id == _settings.SelectedProfileId)?.Id
            ?? _profiles.FirstOrDefault()?.Id;
        SaveProfiles(nextActiveId);
        RefreshNodePicker();
        SyncNodePickerDisplay();
        RefreshSubscriptionFilterOptions();
        ProfilesGrid.SelectedItem = _profiles.FirstOrDefault(profile => profile.Id == nextActiveId);
    }

    private void CtxTcpTest_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedProfiles();
        if (selected.Count > 0)
        {
            _ = RunTcpLatencyTestsAsync(selected, parallel: true);
        }
    }

    private void CtxCopySelectedLinks_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedProfiles();
        if (selected.Count == 0)
        {
            return;
        }

        Clipboard.SetText(string.Join(Environment.NewLine, selected.Select(BuildShareLink)));
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
            if (!string.Equals(existing.Address, saved.Address, StringComparison.OrdinalIgnoreCase))
            {
                existing.SetRegion("");
            }

            CopyProfile(saved, existing);
            existing.ResetLatency();
        }

        SaveProfiles(saved.Id);
        RefreshNodePicker();
        ProfilesGrid.Items.Refresh();
        ProfilesGrid.SelectedItem = _profiles.FirstOrDefault(p => p.Id == saved.Id);
        ScheduleRegionEnrichment([existing ?? saved]);
    }

    private void OpenImportDialog()
    {
        var dialog = new ImportDialog { Owner = this };
        if (dialog.ShowDialog() != true || dialog.ImportResult is not SubscriptionImportResult result)
        {
            return;
        }

        AddImportedProfiles(result);
    }

    private void RefreshNodePicker()
    {
        NodePickerCombo.ItemsSource = null;
        NodePickerCombo.ItemsSource = _profiles;
        NodePickerCombo.IsEditable = true;
        SyncNodePickerDisplay();
    }

    private void NodeListNavButton_Click(object sender, RoutedEventArgs e) => ShowNodePage();

    private void CtxNewNode_Click(object sender, RoutedEventArgs e) => ShowNewNodePage();

    private void CtxImportNode_Click(object sender, RoutedEventArgs e) => ShowImportPage();

    private void ImportNodeNavButton_Click(object sender, RoutedEventArgs e) => ShowImportPage();

    private void NewNodeNavButton_Click(object sender, RoutedEventArgs e) => ShowNewNodePage();

    private void ExportNodeNavButton_Click(object sender, RoutedEventArgs e) => ShowExportPage();

    private void UpdateNavButton_Click(object sender, RoutedEventArgs e) => ShowUpdatePage();

    private void GithubNavButton_Click(object sender, RoutedEventArgs e) => OpenPath(ProjectUrl);

    private void LogNavButton_Click(object sender, RoutedEventArgs e) => ShowLogPage();

    private void ShowLogPage()
    {
        RefreshLogView();
        ShowPage(LogPageScroll, LogNavButton);
    }

    private void ShowLogPageFromTray()
    {
        Dispatcher.Invoke(() =>
        {
            ShowMainWindow();
            ShowLogPage();
        });
    }

    private void RefreshLogView()
    {
        if (LogViewText is null)
        {
            return;
        }

        LogViewText.Text = DiagnosticLogService.GetDisplayText(BuildLogFilter());
        ScrollLogToEnd();
    }

    private void ScrollLogToEnd()
    {
        if (LogViewText is null)
        {
            return;
        }

        LogViewText.CaretIndex = LogViewText.Text.Length;
        LogViewText.ScrollToEnd();
        var scrollViewer = FindVisualChild<ScrollViewer>(LogViewText);
        scrollViewer?.ScrollToEnd();
    }

    private LogFilter BuildLogFilter()
    {
        return new LogFilter
        {
            ShowInfo = LogFilterInfo?.IsChecked == true,
            ShowWarn = LogFilterWarn?.IsChecked == true,
            ShowError = LogFilterError?.IsChecked == true,
            ShowCrash = LogFilterCrash?.IsChecked == true,
            ShowSystem = LogFilterSystem?.IsChecked == true,
            ShowTraffic = LogFilterTraffic?.IsChecked == true
        };
    }

    private void LogFilter_Changed(object sender, RoutedEventArgs e)
    {
        RefreshLogView();
    }

    private void DiagnosticLogService_EntryAdded(LogEntry entry)
    {
        Dispatcher.Invoke(() =>
        {
            if (LogPageScroll.Visibility != Visibility.Visible)
            {
                return;
            }

            if (!entry.Matches(BuildLogFilter()))
            {
                return;
            }

            if (LogViewText.Text is "No logs match the current filters." or "No log records yet.")
            {
                LogViewText.Text = entry.DisplayLine;
            }
            else
            {
                LogViewText.AppendText(Environment.NewLine + entry.DisplayLine);
            }

            ScrollLogToEnd();
        });
    }

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

    private void SettingsNavButton_Click(object sender, RoutedEventArgs e) => ShowSettingsPage();

    private void ShowSettingsPage()
    {
        SyncRunAtStartupFromSettings();
        SyncAllowLanAccessFromSettings();
        ShowPage(SettingsPageScroll, SettingsNavButton);
    }

    private void ShowAboutPage()
    {
        LoadAboutPageInfo();
        ShowPage(AboutPageScroll, AboutNavButton);
        UpdateAboutResponsiveLayout(GetAboutContentWidth());
        _aboutRuntimeTimer.Start();
    }

    private void AboutRuntimeTimer_Tick(object? sender, EventArgs e)
    {
        UpdateAboutRuntimeText();
    }

    private void UpdateAboutRuntimeText()
    {
        if (!_isUiReady || AboutRuntimeText is null)
        {
            return;
        }

        AboutRuntimeText.Text = FormatRuntimeClock();
    }

    private void ShowPage(ScrollViewer page, System.Windows.Controls.Button? activeButton)
    {
        NodePageScroll.Visibility = Visibility.Collapsed;
        NewNodePageScroll.Visibility = Visibility.Collapsed;
        ImportPageScroll.Visibility = Visibility.Collapsed;
        ExportPageScroll.Visibility = Visibility.Collapsed;
        NodeTestPageScroll.Visibility = Visibility.Collapsed;
        UpdatePageScroll.Visibility = Visibility.Collapsed;
        SettingsPageScroll.Visibility = Visibility.Collapsed;
        AboutPageScroll.Visibility = Visibility.Collapsed;
        LogPageScroll.Visibility = Visibility.Collapsed;
        LoginPageScroll.Visibility = Visibility.Collapsed;
        RegisterPageScroll.Visibility = Visibility.Collapsed;
        AuthDialogOverlay.Visibility = Visibility.Collapsed;
        page.Visibility = Visibility.Visible;

        if (!ReferenceEquals(page, AboutPageScroll))
        {
            _aboutRuntimeTimer.Stop();
        }

        foreach (var button in new[] { NodeListNavButton, NewNodeNavButton, ImportNodeNavButton, ExportNodeNavButton, NodeTestNavButton, SettingsNavButton, LogNavButton, AboutNavButton, UpdateNavButton })
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
        UpdateAboutRuntimeText();
        AboutCoreVersionText.Text = GetExecutableVersion(CoreRunner.ResolveCorePath("xray.exe"), "version");
        AboutTunRuntimeText.Text = File.Exists(TunService.SingBoxPath)
            ? GetExecutableVersion(TunService.SingBoxPath, "version")
            : "未安装";
        AboutOperatingSystemText.Text = SystemInfoService.GetOperatingSystemDescription();
        AboutSystemProxyText.Text = SystemInfoService.GetSystemProxyAddress();
        AboutLanAddressText.Text = SystemInfoService.GetPrimaryLanIPv4() ?? "未获取";
        AboutLocalPublicIpText.Text = "检测中...";
        SetAboutDirectoryText(AboutConfigDirectoryText, configDirectory);
        SetAboutDirectoryText(AboutRuntimeDirectoryText, runtimeDirectory);
        SetAboutDirectoryText(AboutLogDirectoryText, DiagnosticLogService.LogDirectory);
        UpdateAboutResponsiveLayout(GetAboutContentWidth());
        _ = LoadAboutPublicIpAsync();
    }

    private void AboutPageScroll_SizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateAboutResponsiveLayout(e.NewSize.Width);

    private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (AboutPageScroll.Visibility == Visibility.Visible)
        {
            UpdateAboutResponsiveLayout(GetAboutContentWidth());
        }
    }

    private double GetAboutContentWidth()
    {
        if (AboutPageScroll is null)
        {
            return ActualWidth;
        }

        return AboutPageScroll.ViewportWidth > 0
            ? AboutPageScroll.ViewportWidth
            : AboutPageScroll.ActualWidth;
    }

    private void UpdateAboutResponsiveLayout(double availableWidth)
    {
        if (!_isUiReady || AboutSummaryGrid is null || AboutFooterGrid is null)
        {
            return;
        }

        var stacked = availableWidth < AboutTwoColumnBreakpoint;
        ApplyAboutTwoColumnLayout(AboutSummaryGrid, AboutSummaryLeftPanel, AboutSummaryRightPanel, stacked, leftColumnWeight: 1);
        ApplyAboutTwoColumnLayout(AboutFooterGrid, AboutDirectoryPanel, AboutLicensePanel, stacked, leftColumnWeight: 1);
    }

    private static void ApplyAboutTwoColumnLayout(
        Grid grid,
        FrameworkElement leftPanel,
        FrameworkElement rightPanel,
        bool stacked,
        int leftColumnWeight)
    {
        if (grid.ColumnDefinitions.Count < 3)
        {
            return;
        }

        if (stacked)
        {
            Grid.SetColumn(leftPanel, 0);
            Grid.SetRow(leftPanel, 0);
            Grid.SetColumn(rightPanel, 0);
            Grid.SetRow(rightPanel, 1);
            grid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            grid.ColumnDefinitions[1].Width = new GridLength(0);
            grid.ColumnDefinitions[2].Width = new GridLength(0);
            leftPanel.Margin = new Thickness(0, 0, 0, 16);
            rightPanel.Margin = new Thickness(0);
            return;
        }

        Grid.SetColumn(leftPanel, 0);
        Grid.SetRow(leftPanel, 0);
        Grid.SetColumn(rightPanel, 2);
        Grid.SetRow(rightPanel, 0);
        grid.ColumnDefinitions[0].Width = new GridLength(leftColumnWeight, GridUnitType.Star);
        grid.ColumnDefinitions[1].Width = new GridLength(24);
        grid.ColumnDefinitions[2].Width = new GridLength(1, GridUnitType.Star);
        leftPanel.Margin = new Thickness(0);
        rightPanel.Margin = new Thickness(0);
    }

    private static void SetAboutDirectoryText(TextBlock textBlock, string path)
    {
        textBlock.Text = path;
        textBlock.ToolTip = path;
    }

    private async Task LoadAboutPublicIpAsync()
    {
        var ip = await SystemInfoService.GetLocalPublicIpAsync();
        if (!_isUiReady || AboutLocalPublicIpText is null)
        {
            return;
        }

        AboutLocalPublicIpText.Text = ip;
    }

    private void AboutOpenLogButton_Click(object sender, RoutedEventArgs e) => DiagnosticLogService.OpenLogDirectory();

    private void AboutOpenConfigButton_Click(object sender, RoutedEventArgs e)
    {
        var directory = GetConfigDirectory();
        Directory.CreateDirectory(directory);
        OpenPath(directory);
    }

    private void AboutOpenRuntimeButton_Click(object sender, RoutedEventArgs e) => OpenPath(Path.Combine(AppContext.BaseDirectory, "cores"));

    private async void UpdatePageCheckButton_Click(object sender, RoutedEventArgs e)
    {
        UpdatePageCheckButton.IsEnabled = false;
        UpdateProgressPanel.Visibility = Visibility.Collapsed;
        UpdateProgressBar.IsIndeterminate = false;
        UpdateProgressBar.Value = 0;
        try
        {
            UpdateStatusText.Text = "正在检查更新...";
            var release = await GetLatestReleaseAsync();
            var currentVersion = GetCurrentVersion();
            if (CompareVersionText(release.TagName, currentVersion) <= 0)
            {
                UpdateStatusText.Text = $"当前已是最新版本：{currentVersion}。";
                return;
            }

            var installer = release.Assets
                .FirstOrDefault(asset => asset.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                                         asset.Name.Contains("Setup", StringComparison.OrdinalIgnoreCase));
            if (installer is null)
            {
                UpdateStatusText.Text = $"发现新版本 {release.TagName}，但 Release Assets 中没有找到安装包。请上传 Nexora-Setup-{release.TagName.TrimStart('v', 'V')}.exe。";
                return;
            }

            UpdateStatusText.Text = $"发现新版本 {release.TagName}，正在下载 {installer.Name}。";
            UpdateProgressPanel.Visibility = Visibility.Visible;
            UpdateProgressText.Text = $"准备下载 {installer.Name}（{FormatBytes(installer.Size)}）";
            var installerPath = await DownloadInstallerAsync(installer, new Progress<DownloadProgress>(UpdateDownloadProgress));
            UpdateProgressText.Text = $"下载完成：{FormatBytes(installer.Size)}";
            UpdateStatusText.Text = "下载完成，正在启动安装程序。";
            Process.Start(new ProcessStartInfo
            {
                FileName = installerPath,
                UseShellExecute = true
            });
            _isExiting = true;
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Error("Update check or download failed.", ex);
            UpdateStatusText.Text = $"检查更新失败：无法连接 GitHub Release 下载域名，可能是当前网络阻止了 release-assets.githubusercontent.com:443。请先启用代理并确认代理可用后重试。详细信息：{ex.Message}";
        }
        finally
        {
            UpdatePageCheckButton.IsEnabled = true;
        }
    }

    private async Task<ReleaseInfo> GetLatestReleaseAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
        request.Headers.UserAgent.ParseAdd("Nexora");
        using var client = CreateUpdateHttpClient();
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync();
        using var document = await JsonDocument.ParseAsync(stream);
        var root = document.RootElement;
        var tagName = root.GetProperty("tag_name").GetString() ?? "";
        var htmlUrl = root.GetProperty("html_url").GetString() ?? ProjectUrl;
        var assets = new List<ReleaseAsset>();
        if (root.TryGetProperty("assets", out var assetsElement))
        {
            foreach (var asset in assetsElement.EnumerateArray())
            {
                assets.Add(new ReleaseAsset(
                    asset.GetProperty("name").GetString() ?? "",
                    asset.GetProperty("browser_download_url").GetString() ?? "",
                    asset.TryGetProperty("size", out var sizeElement) ? sizeElement.GetInt64() : 0));
            }
        }

        if (string.IsNullOrWhiteSpace(tagName))
        {
            throw new InvalidOperationException("Release 信息缺少版本号。");
        }

        return new ReleaseInfo(tagName, htmlUrl, assets);
    }

    private async Task<string> DownloadInstallerAsync(ReleaseAsset asset, IProgress<DownloadProgress> progress)
    {
        if (string.IsNullOrWhiteSpace(asset.DownloadUrl))
        {
            throw new InvalidOperationException("安装包下载地址为空。");
        }

        var directory = Path.Combine(Path.GetTempPath(), "Nexora", "updates");
        Directory.CreateDirectory(directory);
        var targetPath = Path.Combine(directory, asset.Name);

        using var client = CreateUpdateHttpClient();
        using var response = await client.GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        var totalBytes = response.Content.Headers.ContentLength ?? asset.Size;
        progress.Report(new DownloadProgress(0, totalBytes));

        await using var source = await response.Content.ReadAsStreamAsync();
        await using var target = File.Create(targetPath);
        var buffer = new byte[81920];
        long downloadedBytes = 0;
        int bytesRead;
        while ((bytesRead = await source.ReadAsync(buffer)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, bytesRead));
            downloadedBytes += bytesRead;
            progress.Report(new DownloadProgress(downloadedBytes, totalBytes));
        }

        return targetPath;
    }

    private void UpdateDownloadProgress(DownloadProgress progress)
    {
        if (progress.TotalBytes > 0)
        {
            var percentage = Math.Clamp(progress.DownloadedBytes * 100d / progress.TotalBytes, 0, 100);
            UpdateProgressBar.IsIndeterminate = false;
            UpdateProgressBar.Value = percentage;
            UpdateProgressText.Text = $"正在下载：{FormatBytes(progress.DownloadedBytes)} / {FormatBytes(progress.TotalBytes)}";
            return;
        }

        UpdateProgressBar.IsIndeterminate = true;
        UpdateProgressText.Text = $"正在下载：{FormatBytes(progress.DownloadedBytes)} / 未知大小";
    }

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
            ProfileMetadataHelper.ApplyNew(profile);

            _profiles.Add(profile);
            SaveProfiles(profile.Id);
            RefreshNodePicker();
            ProfilesGrid.SelectedItem = profile;
            ShowNodePage();
            ScheduleRegionEnrichment([profile]);
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
            await ImportContentAsync(InlineImportBox.Text);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private async Task ImportContentAsync(string content)
    {
        InlineImportBox.Text = content;
        var result = await SubscriptionImportService.ImportAsync(content);
        AddImportedProfiles(result);
    }

    private async void InlineOpenQrImageButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择二维码图片",
            Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|所有文件|*.*"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var content = DecodeQrCodeFromFile(dialog.FileName);
            await ImportContentAsync(content);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private async void InlineScanClipboardQrButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Clipboard.ContainsText())
            {
                await ImportContentAsync(Clipboard.GetText());
                return;
            }

            if (!Clipboard.ContainsImage())
            {
                throw new InvalidOperationException("剪贴板中没有可识别的链接或二维码图片。");
            }

            var image = Clipboard.GetImage() ?? throw new InvalidOperationException("无法读取剪贴板图片。");
            var content = DecodeQrCodeFromBitmapSource(image);
            await ImportContentAsync(content);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private void AddImportedProfiles(SubscriptionImportResult result)
    {
        RegisterSubscriptionSource(result);

        foreach (var profile in result.Profiles)
        {
            if (result.TrafficInfo is not null && !string.IsNullOrWhiteSpace(profile.SubscriptionName))
            {
                profile.SubscriptionUploadBytes = result.TrafficInfo.UploadBytes;
                profile.SubscriptionDownloadBytes = result.TrafficInfo.DownloadBytes;
                profile.SubscriptionTotalBytes = result.TrafficInfo.TotalBytes;
            }

            _profiles.Add(profile);
        }

        ApplySubscriptionTrafficInfo(result.TrafficInfo);
        var last = result.Profiles.LastOrDefault();
        SaveProfiles(last?.Id ?? _settings.SelectedProfileId);
        RefreshNodePicker();
        ProfilesGrid.Items.Refresh();
        if (last is not null)
        {
            ProfilesGrid.SelectedItem = last;
        }

        ShowNodePage();
        ScheduleRegionEnrichment(result.Profiles);
        RefreshRegionFilterOptions();
        RefreshSubscriptionFilterOptions();

        if (result.Profiles.Count > 0)
        {
            _ = RunTcpLatencyTestsAsync(result.Profiles, parallel: true);
        }
    }

    private void RegisterSubscriptionSource(SubscriptionImportResult result)
    {
        if (string.IsNullOrWhiteSpace(result.SourceUrl) || string.IsNullOrWhiteSpace(result.SubscriptionName))
        {
            return;
        }

        _settings.SubscriptionSources.TryGetValue(result.SubscriptionName, out var existing);
        _settings.SubscriptionSources[result.SubscriptionName] = new SubscriptionSource
        {
            Url = result.SourceUrl,
            AutoRefreshMinutes = existing?.AutoRefreshMinutes
        };
        _settingsStore.Save(_settings);
    }

    private void RestoreSubscriptionAutoRefreshTimers()
    {
        foreach (var timer in _subscriptionRefreshTimers.Values)
        {
            timer.Stop();
        }

        _subscriptionRefreshTimers.Clear();

        foreach (var (subscriptionName, source) in _settings.SubscriptionSources)
        {
            if (source.AutoRefreshMinutes is int minutes && minutes > 0)
            {
                StartSubscriptionAutoRefresh(subscriptionName, minutes, save: false);
            }
        }
    }

    private void StartSubscriptionAutoRefresh(string subscriptionName, int minutes, bool save = true)
    {
        if (_subscriptionRefreshTimers.TryGetValue(subscriptionName, out var existingTimer))
        {
            existingTimer.Stop();
            _subscriptionRefreshTimers.Remove(subscriptionName);
        }

        if (!_settings.SubscriptionSources.TryGetValue(subscriptionName, out var source))
        {
            source = new SubscriptionSource();
            _settings.SubscriptionSources[subscriptionName] = source;
        }

        source.AutoRefreshMinutes = minutes;
        if (save)
        {
            _settingsStore.Save(_settings);
        }

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(minutes) };
        timer.Tick += async (_, _) => await RefreshSubscriptionAsync(subscriptionName);
        timer.Start();
        _subscriptionRefreshTimers[subscriptionName] = timer;
    }

    private void StopSubscriptionAutoRefresh(string subscriptionName, bool save = true)
    {
        if (_subscriptionRefreshTimers.TryGetValue(subscriptionName, out var timer))
        {
            timer.Stop();
            _subscriptionRefreshTimers.Remove(subscriptionName);
        }

        if (_settings.SubscriptionSources.TryGetValue(subscriptionName, out var source))
        {
            source.AutoRefreshMinutes = null;
            if (save)
            {
                _settingsStore.Save(_settings);
            }
        }
    }

    private async void SubscriptionGroupRefreshButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string subscriptionName })
        {
            await RefreshSubscriptionAsync(subscriptionName);
        }
    }

    private void SubscriptionContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.ContextMenu menu || menu.PlacementTarget is not DependencyObject target)
        {
            if (sender is System.Windows.Controls.ContextMenu invalidMenu)
            {
                invalidMenu.IsOpen = false;
            }

            return;
        }

        var subscriptionName = FindSubscriptionGroupName(target);
        if (string.IsNullOrWhiteSpace(subscriptionName) ||
            string.Equals(subscriptionName, "手动", StringComparison.OrdinalIgnoreCase))
        {
            menu.IsOpen = false;
            return;
        }

        _subscriptionContextMenuName = subscriptionName;
    }

    private async void SubscriptionRefreshMenu_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_subscriptionContextMenuName))
        {
            await RefreshSubscriptionAsync(_subscriptionContextMenuName);
        }
    }

    private void SubscriptionAutoRefreshOff_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_subscriptionContextMenuName))
        {
            StopSubscriptionAutoRefresh(_subscriptionContextMenuName);
        }
    }

    private void SubscriptionAutoRefreshPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem { Tag: string tag } ||
            !int.TryParse(tag, out var minutes) ||
            string.IsNullOrWhiteSpace(_subscriptionContextMenuName))
        {
            return;
        }

        StartSubscriptionAutoRefresh(_subscriptionContextMenuName, minutes);
    }

    private void SubscriptionAutoRefreshCustom_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_subscriptionContextMenuName))
        {
            return;
        }

        _settings.SubscriptionSources.TryGetValue(_subscriptionContextMenuName, out var source);
        var dialog = new DurationPromptDialog(_subscriptionContextMenuName, source?.AutoRefreshMinutes)
        {
            Owner = this
        };

        if (dialog.ShowDialog() == true)
        {
            StartSubscriptionAutoRefresh(_subscriptionContextMenuName, dialog.Minutes);
        }
    }

    private static string? FindSubscriptionGroupName(DependencyObject source)
    {
        var current = source;
        while (current is not null)
        {
            if (current is FrameworkElement { Tag: string name } && !string.IsNullOrWhiteSpace(name))
            {
                return name;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private async Task RefreshSubscriptionAsync(string subscriptionName)
    {
        if (string.Equals(subscriptionName, "手动", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!_settings.SubscriptionSources.TryGetValue(subscriptionName, out var source) ||
            string.IsNullOrWhiteSpace(source.Url))
        {
            MessageBox.Show("该订阅没有保存原始地址，请重新导入订阅链接。", "Nexora", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var result = await SubscriptionImportService.ImportAsync(source.Url);
            var previousActive = GetSelectedProfileOrNull();
            var removed = _profiles
                .Where(profile => string.Equals(profile.SubscriptionName, subscriptionName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var profile in removed)
            {
                _profiles.Remove(profile);
            }

            foreach (var profile in result.Profiles)
            {
                profile.SubscriptionName = subscriptionName;
                profile.SubscriptionUpdatedAt = DateTime.Now;
                profile.UpdatedAt = DateTime.Now;
                if (result.TrafficInfo is not null)
                {
                    profile.SubscriptionUploadBytes = result.TrafficInfo.UploadBytes;
                    profile.SubscriptionDownloadBytes = result.TrafficInfo.DownloadBytes;
                    profile.SubscriptionTotalBytes = result.TrafficInfo.TotalBytes;
                }

                _profiles.Add(profile);
            }

            ApplySubscriptionTrafficInfo(result.TrafficInfo);

            string? nextActiveId = previousActive?.Id;
            if (previousActive is not null &&
                string.Equals(previousActive.SubscriptionName, subscriptionName, StringComparison.OrdinalIgnoreCase))
            {
                var replacement = _profiles.FirstOrDefault(profile =>
                    profile.Address == previousActive.Address &&
                    profile.Port == previousActive.Port &&
                    string.Equals(profile.Protocol, previousActive.Protocol, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(profile.SubscriptionName, subscriptionName, StringComparison.OrdinalIgnoreCase));
                nextActiveId = replacement?.Id ?? _profiles.FirstOrDefault()?.Id;
            }
            else if (_profiles.All(profile => profile.Id != nextActiveId))
            {
                nextActiveId = _profiles.FirstOrDefault()?.Id;
            }

            SaveProfiles(nextActiveId);
            RefreshNodePicker();
            SyncNodePickerDisplay();
            RefreshRegionFilterOptions();
            RefreshSubscriptionFilterOptions();
            ProfilesGrid.Items.Refresh();
            ScheduleRegionEnrichment(result.Profiles);

            if (result.Profiles.Count > 0)
            {
                await RunTcpLatencyTestsAsync(result.Profiles, parallel: true);
            }
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private static string DecodeQrCodeFromFile(string path)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri(path, UriKind.Absolute);
        bitmap.EndInit();
        bitmap.Freeze();
        return DecodeQrCodeFromBitmapSource(bitmap);
    }

    private static string DecodeQrCodeFromBitmapSource(BitmapSource source)
    {
        var bitmap = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);

        var reader = new BarcodeReaderGeneric
        {
            AutoRotate = true,
            Options = new DecodingOptions
            {
                TryHarder = true,
                PossibleFormats = [BarcodeFormat.QR_CODE]
            }
        };

        var result = reader.Decode(pixels, bitmap.PixelWidth, bitmap.PixelHeight, RGBLuminanceSource.BitmapFormat.BGRA32);
        if (result is null || string.IsNullOrWhiteSpace(result.Text))
        {
            throw new InvalidOperationException("没有从图片中识别到二维码内容。");
        }

        return result.Text.Trim();
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

    private void RemoveUnavailableButton_Click(object sender, RoutedEventArgs e)
    {
        RemoveUnavailableProfiles();
    }

    private void CtxRemoveUnavailableNodes_Click(object sender, RoutedEventArgs e)
    {
        RemoveUnavailableProfiles();
    }

    private void RemoveDuplicateButton_Click(object sender, RoutedEventArgs e)
    {
        RemoveDuplicateProfiles();
    }

    private void CtxRemoveDuplicateNodes_Click(object sender, RoutedEventArgs e)
    {
        RemoveDuplicateProfiles();
    }

    private void RemoveUnavailableProfiles()
    {
        if (_profiles.Count == 0)
        {
            MessageBox.Show("当前没有可移除的节点。", "Nexora", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var unavailable = _profiles.Where(IsUnavailableProfile).ToList();
        if (unavailable.Count == 0)
        {
            MessageBox.Show("没有已测速为不可用的节点。请先执行测速或等待启动自动测速完成。", "Nexora", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var result = MessageBox.Show(
            $"将移除 {unavailable.Count} 个已测速为不可用的节点，是否继续？",
            "移除不可用节点",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        var selectedId = _settings.SelectedProfileId;
        var removedActive = unavailable.Any(profile => profile.Id == selectedId);
        foreach (var profile in unavailable)
        {
            _profiles.Remove(profile);
        }

        if (_profiles.All(profile => profile.Id != selectedId))
        {
            selectedId = _profiles.FirstOrDefault()?.Id;
        }

        SaveProfiles(selectedId);
        RefreshNodePicker();
        RefreshProfilesView();
        RefreshRegionFilterOptions();
        RefreshSubscriptionFilterOptions();
        var selected = _profiles.FirstOrDefault(profile => profile.Id == selectedId);
        ProfilesGrid.SelectedItem = selected;
        UpdateNodeStatusBar(selected);

        if (removedActive && _coreService.IsRunning && selected is not null)
        {
            _ = RestartCoreAsync();
        }

        MessageBox.Show($"已移除 {unavailable.Count} 个不可用节点，当前保留 {_profiles.Count} 个节点。", "Nexora", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void RemoveDuplicateProfiles()
    {
        if (_profiles.Count == 0)
        {
            MessageBox.Show("当前没有可去重的节点。", "Nexora", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var duplicates = _profiles.Where(profile => !seen.Add(BuildProfileKey(profile))).ToList();
        if (duplicates.Count == 0)
        {
            MessageBox.Show("没有发现重复节点。", "Nexora", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var result = MessageBox.Show(
            $"将移除 {duplicates.Count} 个重复节点，并保留首次出现的节点，是否继续？",
            "去除重复节点",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        var selectedId = _settings.SelectedProfileId;
        var removedActive = duplicates.Any(profile => profile.Id == selectedId);
        foreach (var profile in duplicates)
        {
            _profiles.Remove(profile);
        }

        if (_profiles.All(profile => profile.Id != selectedId))
        {
            selectedId = _profiles.FirstOrDefault()?.Id;
        }

        SaveProfiles(selectedId);
        RefreshNodePicker();
        RefreshProfilesView();
        RefreshRegionFilterOptions();
        RefreshSubscriptionFilterOptions();
        var selected = _profiles.FirstOrDefault(profile => profile.Id == selectedId);
        ProfilesGrid.SelectedItem = selected;
        UpdateNodeStatusBar(selected);

        if (removedActive && _coreService.IsRunning && selected is not null)
        {
            _ = RestartCoreAsync();
        }

        MessageBox.Show($"已移除 {duplicates.Count} 个重复节点，当前保留 {_profiles.Count} 个节点。", "Nexora", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void CtxMaintainNode_Click(object sender, RoutedEventArgs e)
    {
        if (_profiles.Count == 0)
        {
            MessageBox.Show("当前没有需要维护的节点。", "Nexora", MessageBoxButton.OK, MessageBoxImage.Information);
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
        MessageBox.Show($"维护完成：清理 {before - _profiles.Count} 个节点，保留 {_profiles.Count} 个节点。", "Nexora", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void CtxAllTcpTest_Click(object sender, RoutedEventArgs e)
    {
        if (_profiles.Count == 0)
        {
            return;
        }

        _ = RunTcpLatencyTestsAsync(_profiles.ToList(), parallel: true);
    }

    private async void TestCurrentLatencyButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedProfiles();
        if (selected.Count == 0)
        {
            MessageBox.Show("请先选择一个节点。", "Nexora", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await RunTcpLatencyTestsAsync(selected, parallel: true);
    }

    private void TestAllLatencyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_profiles.Count == 0)
        {
            return;
        }

        _ = RunTcpLatencyTestsAsync(_profiles.ToList(), parallel: true);
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

            UpdateNodeStatusBar(GetSelectedProfileOrNull());
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
        TestCurrentLatencyButton.IsEnabled = enabled;
        TestAllLatencyButton.IsEnabled = enabled;
        RemoveUnavailableButton.IsEnabled = enabled;
        RemoveDuplicateButton.IsEnabled = enabled;
        EditRoutingButton.IsEnabled = enabled;
    }

    private static bool IsUnavailableProfile(VmessProfile profile)
    {
        return profile.TcpLatencyDisplay == "Timeout";
    }

    private VmessProfile GetActiveProfile()
    {
        return GetSelectedProfileOrNull()
            ?? throw new InvalidOperationException("请先选择活动节点。");
    }

    private VmessProfile? GetSelectedProfileOrNull()
    {
        if (string.IsNullOrWhiteSpace(_settings.SelectedProfileId))
        {
            return null;
        }

        return _profiles.FirstOrDefault(p => p.Id == _settings.SelectedProfileId);
    }

    private VmessProfile? GetCurrentProfileOrNull() => GetSelectedProfileOrNull();

    private static string FormatSystemProxyMode(string mode)
    {
        return mode switch
        {
            "Clear" => "关闭",
            "Auto" => "开启",
            "Unchanged" => "不改变",
            "Pac" => "PAC",
            _ => "自动"
        };
    }

    private static string FormatRoutingMode(string mode)
    {
        return mode switch
        {
            "Global" => "全局代理",
            "BypassChina" => "绕过大陆",
            "BypassLan" => "绕过局域网",
            "Direct" => "直连模式",
            "Custom" => "自定义规则",
            _ => "绕过大陆"
        };
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
        target.Region = source.Region;
        target.SubscriptionName = source.SubscriptionName;
        target.SubscriptionUpdatedAt = source.SubscriptionUpdatedAt;
        target.SubscriptionUploadBytes = source.SubscriptionUploadBytes;
        target.SubscriptionDownloadBytes = source.SubscriptionDownloadBytes;
        target.SubscriptionTotalBytes = source.SubscriptionTotalBytes;
        target.XpanelExpiryTime = source.XpanelExpiryTime;
        target.XpanelTotalBytes = source.XpanelTotalBytes;
        target.XpanelUsedBytes = source.XpanelUsedBytes;
        target.XpanelRemainingBytes = source.XpanelRemainingBytes;
        target.UpdatedAt = DateTime.Now;
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
        return string.IsNullOrWhiteSpace(sanitized) ? "Nexora-Node" : sanitized;
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
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Nexora");
    }

    private static string GetCurrentVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString(3)
            ?? "未知";
        return version.Split('+')[0];
    }

    private static string FormatRuntimeClock()
    {
        var elapsed = DateTime.Now - AppStartTime;
        return $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
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
            MessageBox.Show(ex.Message, "Nexora", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private sealed record ReleaseInfo(string TagName, string HtmlUrl, List<ReleaseAsset> Assets);

    private sealed record ReleaseAsset(string Name, string DownloadUrl, long Size);

    private sealed record DownloadProgress(long DownloadedBytes, long TotalBytes);

    private static void ShowError(Exception exception)
    {
        DiagnosticLogService.Error(exception.Message, exception);
        MessageBox.Show(exception.Message, "Nexora", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
