using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using NaiwaProxy.Dialogs;
using NaiwaProxy.Models;
using NaiwaProxy.Services;
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
    public static readonly DependencyProperty WebsiteTestCardWidthProperty =
        DependencyProperty.Register(
            nameof(WebsiteTestCardWidth),
            typeof(double),
            typeof(MainWindow),
            new PropertyMetadata(300d));

    public double WebsiteTestCardWidth
    {
        get => (double)GetValue(WebsiteTestCardWidthProperty);
        set => SetValue(WebsiteTestCardWidthProperty, value);
    }

    private const double AboutTwoColumnBreakpoint = 980;
    private const double WebsiteTestCardMinWidth = 220;
    private const double WebsiteTestCardGap = 12;
    private const double WebsiteTestCardAbsoluteMinWidth = 180;
    private static readonly DateTime AppStartTime = DateTime.Now;
    private const string ProjectUrl = "https://github.com/LiWenhui2/NaiwaProxy";
    private const string DefaultChatBackgroundResource = "assets/chat/chat-bg-default.png";
    private readonly SettingsStore _settingsStore = new();
    private readonly CoreService _coreService = new();
    private readonly AuthService _authService = new();
    private readonly BackendWebSocketService _backendWebSocket;
    private readonly AppUpdateDownloadService _appUpdateDownloadService = new();
    private readonly ChatAttachmentDownloadService _chatAttachmentDownloadService = new();
    private readonly ChatMessageStore _chatMessageStore = new();
    private readonly Dictionary<int, string> _chatFileLocalPaths = [];
    private readonly ObservableCollection<NotificationItem> _announcements = [];
    private readonly List<ChatMessage> _chatMessages = [];
    private readonly HashSet<int> _chatMessageIds = [];
    private string? _pendingChatAttachmentPath;
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
    private bool _suppressAutoDownloadNewVersionToggleEvent;
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
    private CancellationTokenSource? _openAiCodexPreWarmCts;
    private readonly DispatcherTimer _registerCodeCooldownTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _forgotPasswordCodeCooldownTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _authRefreshTimer = new() { Interval = TimeSpan.FromMinutes(30) };
    private readonly DispatcherTimer _aboutRuntimeTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _backendWebSocketPingTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly DispatcherTimer _backendWebSocketReconnectTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer _chatToastHideTimer = new() { Interval = TimeSpan.FromSeconds(6) };
    private BitmapImage? _chatMessageIcon;
    private AppUpdateRelease? _cachedLatestRelease;
    private RequiredVersionUpdateDialog? _requiredVersionUpdateDialog;
    private string? _downloadedUpdatePath;
    private int? _downloadedReleaseId;
    private bool _isDownloadingAppUpdate;
    private CancellationTokenSource? _appUpdateDownloadCts;
    private readonly DispatcherTimer _subscriptionGlobalRefreshTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly Dictionary<string, DispatcherTimer> _subscriptionRefreshTimers = new(StringComparer.OrdinalIgnoreCase);
    private bool _subscriptionGlobalRefreshInProgress;
    private int _sideAvatarLoadVersion;
    private bool _isHandlingForceLogout;
    private bool _isHandlingTokenExpired;
    private bool _authRefreshInProgress;
    private int _registerCodeCooldownSeconds;
    private int _forgotPasswordCodeCooldownSeconds;
    private SubscriptionGroupIdentity? _subscriptionContextMenuScope;
    private const double DefaultWindowWidth = 1280;
    private const double DefaultWindowHeight = 720;
    private const double WindowAspectRatio = 16.0 / 9.0;
    private const double WindowWorkAreaMargin = 16;
    private const string InvalidSubscriptionSuffix = "（已失效）";
    private int _unreadAdminChatCount;
    private Drawing.Icon? _trayIconNormal;
    private Drawing.Icon? _trayIconBlank;
    private bool _trayIconBlinkVisible = true;
    private readonly DispatcherTimer _trayBlinkTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };

    public MainWindow(bool startSilent = false)
    {
        _startSilent = startSilent;
        _backendWebSocket = new BackendWebSocketService(
            () => _authService.GetAccessTokenAsync(CancellationToken.None),
            () => string.IsNullOrWhiteSpace(_authService.ApiBaseUrl)
                ? ApiDefaults.NormalizeAuthApiBaseUrl(_settings.AuthApiBaseUrl)
                : _authService.ApiBaseUrl);
        DiagnosticLogService.Startup("MainWindow constructor begin");
        InitializeComponent();
        ApplyInitialWindowBounds();
        RefreshLogView();
        LoadBrandIcon();
        LoadInfoHintIcons();
        LoadNotificationNavIcons();
        InitializeTray();
        _trafficTimer.Tick += TrafficTimer_Tick;
        _coreService.CoreExited += CoreService_CoreExited;
        _authService.AuthStateChanged += AuthService_AuthStateChanged;
        _backendWebSocket.ChatMessageReceived += BackendWebSocket_ChatMessageReceived;
        _backendWebSocket.BroadcastReceived += BackendWebSocket_BroadcastReceived;
        _backendWebSocket.VersionUpdateReceived += BackendWebSocket_VersionUpdateReceived;
        _backendWebSocket.UserProfileUpdated += BackendWebSocket_UserProfileUpdated;
        _backendWebSocket.ForceLogoutReceived += BackendWebSocket_ForceLogoutReceived;
        _backendWebSocket.TokenExpiredReceived += BackendWebSocket_TokenExpiredReceived;
        _backendWebSocket.HeartbeatTimeoutReceived += BackendWebSocket_HeartbeatTimeoutReceived;
        _backendWebSocket.Connected += BackendWebSocket_Connected;
        _backendWebSocket.ConnectionStateChanged += BackendWebSocket_ConnectionStateChanged;
        _backendWebSocket.Disconnected += BackendWebSocket_Disconnected;
        _backendWebSocketPingTimer.Tick += BackendWebSocketPingTimer_Tick;
        _backendWebSocketReconnectTimer.Tick += BackendWebSocketReconnectTimer_Tick;
        _chatToastHideTimer.Tick += ChatToastHideTimer_Tick;
        _trayBlinkTimer.Tick += TrayBlinkTimer_Tick;
        _registerCodeCooldownTimer.Tick += RegisterCodeCooldownTimer_Tick;
        _forgotPasswordCodeCooldownTimer.Tick += ForgotPasswordCodeCooldownTimer_Tick;
        _authRefreshTimer.Tick += AuthRefreshTimer_Tick;
        _aboutRuntimeTimer.Tick += AboutRuntimeTimer_Tick;
        _subscriptionGlobalRefreshTimer.Tick += SubscriptionGlobalRefreshTimer_Tick;
        _profilesView = CollectionViewSource.GetDefaultView(_profiles);
        _profilesView.Filter = FilterProfile;
        _profilesView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(VmessProfile.SubscriptionDisplay)));
        ProfilesGrid.ItemsSource = _profilesView;
        ConfigureImportPlaceholder();
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
            _backendWebSocket.ChatMessageReceived -= BackendWebSocket_ChatMessageReceived;
            _backendWebSocket.BroadcastReceived -= BackendWebSocket_BroadcastReceived;
            _backendWebSocket.VersionUpdateReceived -= BackendWebSocket_VersionUpdateReceived;
            _backendWebSocket.UserProfileUpdated -= BackendWebSocket_UserProfileUpdated;
            _backendWebSocket.ForceLogoutReceived -= BackendWebSocket_ForceLogoutReceived;
            _backendWebSocket.TokenExpiredReceived -= BackendWebSocket_TokenExpiredReceived;
            _backendWebSocket.HeartbeatTimeoutReceived -= BackendWebSocket_HeartbeatTimeoutReceived;
            _backendWebSocket.Connected -= BackendWebSocket_Connected;
            _backendWebSocket.ConnectionStateChanged -= BackendWebSocket_ConnectionStateChanged;
            _backendWebSocket.Disconnected -= BackendWebSocket_Disconnected;
            _backendWebSocketPingTimer.Stop();
            _backendWebSocketPingTimer.Tick -= BackendWebSocketPingTimer_Tick;
            _backendWebSocketReconnectTimer.Stop();
            _backendWebSocketReconnectTimer.Tick -= BackendWebSocketReconnectTimer_Tick;
            _chatToastHideTimer.Stop();
            _chatToastHideTimer.Tick -= ChatToastHideTimer_Tick;
            _trayBlinkTimer.Stop();
            _trayBlinkTimer.Tick -= TrayBlinkTimer_Tick;
            _trayIconBlank?.Dispose();
            _appUpdateDownloadCts?.Cancel();
            _appUpdateDownloadCts?.Dispose();
            _backendWebSocket.Dispose();
            _registerCodeCooldownTimer.Stop();
            _registerCodeCooldownTimer.Tick -= RegisterCodeCooldownTimer_Tick;
            _forgotPasswordCodeCooldownTimer.Stop();
            _forgotPasswordCodeCooldownTimer.Tick -= ForgotPasswordCodeCooldownTimer_Tick;
            _authRefreshTimer.Stop();
            _authRefreshTimer.Tick -= AuthRefreshTimer_Tick;
            _aboutRuntimeTimer.Stop();
            _aboutRuntimeTimer.Tick -= AboutRuntimeTimer_Tick;
            _subscriptionGlobalRefreshTimer.Stop();
            _subscriptionGlobalRefreshTimer.Tick -= SubscriptionGlobalRefreshTimer_Tick;
            foreach (var timer in _subscriptionRefreshTimers.Values)
            {
                timer.Stop();
            }

            _subscriptionRefreshTimers.Clear();
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

    private void LoadNotificationNavIcons()
    {
        var announcementIcon = TryLoadAppBitmap("assets/icons/announcement.png");
        _chatMessageIcon = TryLoadAppBitmap("assets/icons/message.png");
        ApplyNavTintedIcon(AnnouncementNavIcon, announcementIcon);
        ApplyNavTintedIcon(ContactAdminNavIcon, _chatMessageIcon);
        ApplyTintedIconFill(ContactAdminGuestIcon, _chatMessageIcon);
        ApplyTintedIconFill(ChatEmptyIcon, _chatMessageIcon);

        if (announcementIcon is not null)
        {
            AnnouncementPageIcon.Source = announcementIcon;
            AnnouncementGuestIcon.Source = announcementIcon;
            AnnouncementEmptyIcon.Source = announcementIcon;
        }
    }

    private static void ApplyNavTintedIcon(System.Windows.Shapes.Rectangle iconRect, BitmapImage? bitmap)
    {
        if (bitmap is null)
        {
            return;
        }

        iconRect.OpacityMask = new ImageBrush(bitmap) { Stretch = Stretch.Uniform };
    }

    private static void ApplyTintedIconFill(System.Windows.Shapes.Rectangle iconRect, BitmapImage? bitmap)
    {
        if (bitmap is null)
        {
            return;
        }

        iconRect.OpacityMask = new ImageBrush(bitmap) { Stretch = Stretch.Uniform };
        iconRect.Fill = new SolidColorBrush(Color.FromRgb(100, 116, 139));
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
        AutoDownloadUpdateInfoIcon.Source = bitmap;
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
        _trayIconNormal = icon;
        _trayIconBlank = CreateBlankTrayIcon(icon);

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

    private void ApplyInitialWindowBounds()
    {
        var workArea = SystemParameters.WorkArea;
        var maxWidth = Math.Max(MinWidth, workArea.Width - WindowWorkAreaMargin * 2);
        var maxHeight = Math.Max(MinHeight, workArea.Height - WindowWorkAreaMargin * 2);

        var width = Math.Min(DefaultWindowWidth, maxWidth);
        var height = Math.Min(DefaultWindowHeight, maxHeight);

        if (width / height > WindowAspectRatio)
        {
            width = height * WindowAspectRatio;
        }
        else
        {
            height = width / WindowAspectRatio;
        }

        width = Math.Min(width, maxWidth);
        height = Math.Min(height, maxHeight);

        if (width < MinWidth)
        {
            width = MinWidth;
            height = Math.Min(width / WindowAspectRatio, maxHeight);
        }

        if (height < MinHeight)
        {
            height = MinHeight;
            width = Math.Min(height * WindowAspectRatio, maxWidth);
        }

        Width = width;
        Height = height;
        Left = workArea.Left + Math.Max(0, (workArea.Width - width) / 2);
        Top = workArea.Top + Math.Max(0, (workArea.Height - height) / 2);
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_Loaded;

        ClearStaleLocalSystemProxyIfNeeded();

        if (await _authService.TryRestoreSessionAsync())
        {
            StartAuthRefreshTimer();
            await _authService.RefreshProfileFromServerAsync();
            UpdateAuthSidebar();
            await SyncBackendWebSocketAsync();
            await CheckAppUpdateOnStartupAsync();
            try
            {
                await ReloadCloudSubscriptionsAsync(manualRefresh: false);
                RefreshProfilesView();
            }
            catch (Exception ex)
            {
                DiagnosticLogService.Warning($"Startup cloud subscription sync failed: {ex.Message}");
                ShowCloudSubscriptionFailureMessage($"云端自动更新失败：{ex.Message}");
            }
        }

        StartSubscriptionGlobalAutoRefresh();

        if (_profiles.Count > 0)
        {
            await RunStartupLatencyTestsAsync();
            ApplyActiveProfileSelection(autoSelectIfMissing: true, save: true);
            RefreshProfilesView();
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

            if (MigrateLocalProfileFlags(profile, _settings))
            {
                metadataChanged = true;
            }
        }

        foreach (var source in _settings.SubscriptionSources.Values)
        {
            if (MigrateLocalSubscriptionSource(source))
            {
                metadataChanged = true;
            }
        }

        MigrateLocalSubscriptionSourceKeys(ref metadataChanged);

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

        ApplyActiveProfileSelection(autoSelectIfMissing: true, save: true);
        ConfigureAuthService();
        ApplyTheme();
        ApplyThemeBackground();
        UpdateAuthSidebar();
        UpdateNotificationPagesAuthState();
        UpdateSidebarStatus();
        UpdateTrafficStatsDisplay();
        RefreshRegionFilterOptions();
        RefreshSubscriptionFilterOptions();
        ScheduleRegionEnrichment();
        SyncRunAtStartupFromSettings();
        SyncAllowLanAccessFromSettings();
        SyncAutoDownloadUpdateFromSettings();
        EnsureOpenAiCodexOptimizationApplied();
        ApplyStartupSettings(save: false);
        RestoreSubscriptionAutoRefreshTimers();
        ReconcileSubscriptionTrafficExhaustedState();
        ClearStaleLocalSystemProxyIfNeeded();
        RestoreDownloadedUpdateState();
    }

    private void ClearStaleLocalSystemProxyIfNeeded()
    {
        if (_coreService.IsRunning)
        {
            return;
        }

        if (!SystemProxyService.IsHttpProxyEnabled(_settings.HttpPort))
        {
            return;
        }

        SystemProxyService.DisableProxy();
        DiagnosticLogService.Info("Cleared stale local system proxy because core is not running.");
    }

    private void StartSubscriptionGlobalAutoRefresh()
    {
        _subscriptionGlobalRefreshTimer.Stop();
        _subscriptionGlobalRefreshTimer.Start();
    }

    private async void SubscriptionGlobalRefreshTimer_Tick(object? sender, EventArgs e)
    {
        await RefreshAllSubscriptionsAsync(silent: true);
    }

    private async Task RefreshAllSubscriptionsAsync(bool silent)
    {
        if (_subscriptionGlobalRefreshInProgress)
        {
            return;
        }

        var subscriptionNames = _settings.SubscriptionSources
            .Where(entry =>
                !string.IsNullOrWhiteSpace(entry.Value.Url) &&
                !string.Equals(entry.Key, LocalSubscriptionHelper.LocalLabel, StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.Key)
            .ToList();

        if (subscriptionNames.Count == 0)
        {
            return;
        }

        _subscriptionGlobalRefreshInProgress = true;
        try
        {
            foreach (var subscriptionName in subscriptionNames)
            {
                if (silent && IsSubscriptionTrafficExhausted(subscriptionName))
                {
                    continue;
                }

                await RefreshSubscriptionAsync(subscriptionName, silent);
            }
        }
        finally
        {
            _subscriptionGlobalRefreshInProgress = false;
        }
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
        ScheduleWebsiteTestResponsiveLayout();
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
        Dispatcher.BeginInvoke(ScheduleWebsiteTestResponsiveLayout, DispatcherPriority.Loaded);
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

    private void EnsureOpenAiCodexOptimizationApplied()
    {
        var changed = false;
        if (!_settings.OpenAiCodexOptimizationEnabled)
        {
            OpenAiCodexOptimizationService.Apply(_settings);
            changed = true;
        }
        else if (OpenAiCodexOptimizationService.EnsureRulesMerged(_settings))
        {
            changed = true;
        }

        if (changed)
        {
            _settingsStore.Save(_settings);
        }

        ApplyOpenAiCodexOptimizationUi();
    }

    private void ApplyOpenAiCodexOptimizationUi()
    {
        SelectRoutingCombo(_settings.RoutingMode);
        SelectSystemProxyCombo(_settings.SystemProxyMode);
        UpdateRoutingEditorVisibility();
    }

    private void ScheduleOpenAiCodexPreWarmIfEnabled()
    {
        if (!_settings.OpenAiCodexOptimizationEnabled || !_coreService.IsRunning)
        {
            return;
        }

        _openAiCodexPreWarmCts?.Cancel();
        _openAiCodexPreWarmCts?.Dispose();
        _openAiCodexPreWarmCts = new CancellationTokenSource();
        var token = _openAiCodexPreWarmCts.Token;
        var httpPort = _settings.HttpPort;

        _ = Task.Run(async () =>
        {
            try
            {
                await OpenAiCodexOptimizationService.PreWarmAsync(httpPort, token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                DiagnosticLogService.Warning($"OpenAI/Codex prewarm task failed: {ex.Message}");
            }
        }, token);
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

        var selected = GetSelectedSubscriptionFilterValue();
        SubscriptionFilterCombo.Items.Clear();
        SubscriptionFilterCombo.Items.Add(new ComboBoxItem { Content = "来源：全部订阅" });

        var subscriptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in _profiles)
        {
            var display = profile.SubscriptionDisplay;
            if (string.IsNullOrWhiteSpace(display))
            {
                continue;
            }

            subscriptions[LocalSubscriptionHelper.BuildFilterKey(profile)] = display;
        }

        foreach (var (sourceKey, source) in _settings.SubscriptionSources)
        {
            if (string.IsNullOrWhiteSpace(sourceKey))
            {
                continue;
            }

            subscriptions[sourceKey] = GetSubscriptionFilterDisplayName(sourceKey, source);
        }

        foreach (var subscription in subscriptions.OrderBy(pair => pair.Value, StringComparer.OrdinalIgnoreCase))
        {
            SubscriptionFilterCombo.Items.Add(new ComboBoxItem
            {
                Content = $"来源：{subscription.Value}",
                Tag = subscription.Key
            });
        }

        var matched = SubscriptionFilterCombo.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(GetSubscriptionFilterItemValue(item), selected, StringComparison.OrdinalIgnoreCase));
        SubscriptionFilterCombo.SelectedItem = matched ?? SubscriptionFilterCombo.Items[0];
    }

    private string GetSelectedSubscriptionFilterValue() =>
        GetSubscriptionFilterItemValue(SubscriptionFilterCombo?.SelectedItem as ComboBoxItem);

    private static string GetSubscriptionFilterItemValue(ComboBoxItem? item)
    {
        if (item?.Tag is string tag && !string.IsNullOrWhiteSpace(tag))
        {
            return tag;
        }

        return NormalizeFilterValue(item?.Content?.ToString(), "来源");
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
                var updates = await RegionEnrichmentService.CollectRegionUpdatesAsync(targets, token);
                if (updates.Count == 0 || token.IsCancellationRequested)
                {
                    return;
                }

                await Dispatcher.InvokeAsync(() =>
                {
                    foreach (var (profile, region) in updates)
                    {
                        profile.SetRegion(region);
                    }

                    _settings.Profiles = _profiles.ToList();
                    _settingsStore.Save(_settings);

                    if (_latencyTestBatchActive > 0 || _subscriptionUpdateBatchActive > 0)
                    {
                        return;
                    }

                    RefreshRegionFilterOptions();
                    RefreshSubscriptionFilterOptions();
                }, DispatcherPriority.Background);
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

        var subscription = GetSelectedSubscriptionFilterValue();
        if (!string.IsNullOrWhiteSpace(subscription) &&
            subscription != "全部订阅" &&
            !ProfileMatchesSubscriptionFilter(profile, subscription))
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
        if (!_isUiReady || _profilesView is null || _latencyTestBatchActive > 0 || _subscriptionUpdateBatchActive > 0)
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
            if (_profilesView is ListCollectionView listView)
            {
                listView.IsLiveSorting = false;
            }
        }
        else if (_profilesView is ListCollectionView listView)
        {
            listView.IsLiveSorting = true;
        }
    }

    private static bool SortsByLatency(IEnumerable<SortDescription> sorts) =>
        sorts.Any(sort => string.Equals(sort.PropertyName, nameof(VmessProfile.TcpLatencyMs), StringComparison.Ordinal));

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

    private int _latencyTestBatchActive;
    private int _subscriptionUpdateBatchActive;
    private int _profilesViewFreezeDepth;
    private bool _profilesViewLiveSorting = true;
    private bool _profilesViewLiveFiltering = true;
    private List<SortDescription> _frozenProfilesSort = [];
    private string? _topBarLatencyProfileId;
    private int? _topBarLastLatencyMs;
    private bool _topBarShowingTimeout;
    private const string TopBarLatencyPlaceholder = "\u00a0";

    private void UpdateNodeStatusBar(VmessProfile? profile)
    {
        if (profile is null)
        {
            ApplyNodeHeaderTexts(null);
            _topBarLatencyProfileId = null;
            _topBarLastLatencyMs = null;
            _topBarShowingTimeout = false;
            CurrentTcpLatencyText.Text = TopBarLatencyPlaceholder;
            return;
        }

        ApplyNodeHeaderTexts(profile);
        UpdateTopBarLatencyDisplay(profile);
    }

    private void UpdateTopBarLatencyDisplay(VmessProfile profile, bool force = false)
    {
        if (!force && _latencyTestBatchActive > 0)
        {
            return;
        }

        if (profile.DisplayLatencyMs is int latencyMs)
        {
            ApplyTopBarLatencyText(profile.Id, latencyMs, isTimeout: false);
            return;
        }

        if (profile.TcpLatencyDisplay == "Timeout")
        {
            ApplyTopBarLatencyText(profile.Id, null, isTimeout: true);
            return;
        }

        if (_topBarLatencyProfileId == profile.Id)
        {
            return;
        }

        _topBarLatencyProfileId = profile.Id;
        _topBarLastLatencyMs = null;
        _topBarShowingTimeout = false;
        if (CurrentTcpLatencyText.Text != TopBarLatencyPlaceholder)
        {
            CurrentTcpLatencyText.Text = TopBarLatencyPlaceholder;
        }
    }

    private void ApplyTopBarLatencyText(string profileId, int? latencyMs, bool isTimeout)
    {
        if (latencyMs is int ms)
        {
            var text = $"{ms} ms";
            if (_topBarLatencyProfileId != profileId || _topBarLastLatencyMs != ms || _topBarShowingTimeout)
            {
                if (!string.Equals(CurrentTcpLatencyText.Text, text, StringComparison.Ordinal))
                {
                    CurrentTcpLatencyText.Text = text;
                }

                _topBarLatencyProfileId = profileId;
                _topBarLastLatencyMs = ms;
                _topBarShowingTimeout = false;
            }

            CurrentTcpLatencyText.Foreground = (System.Windows.Media.Brush)FindResource("GreenBrush");
            return;
        }

        if (!isTimeout)
        {
            return;
        }

        if (_topBarLatencyProfileId != profileId || !_topBarShowingTimeout)
        {
            if (!string.Equals(CurrentTcpLatencyText.Text, "Timeout", StringComparison.Ordinal))
            {
                CurrentTcpLatencyText.Text = "Timeout";
            }

            _topBarLatencyProfileId = profileId;
            _topBarLastLatencyMs = null;
            _topBarShowingTimeout = true;
        }

        CurrentTcpLatencyText.Foreground = (System.Windows.Media.Brush)FindResource("RedBrush");
    }

    private void UpdateActiveProfileMarkers(string? activeId)
    {
        foreach (var profile in _profiles)
        {
            profile.SetActive(!string.IsNullOrWhiteSpace(activeId) && profile.Id == activeId);
        }

        var active = string.IsNullOrWhiteSpace(activeId)
            ? null
            : _profiles.FirstOrDefault(p => p.Id == activeId);

        SyncNodePickerDisplay(active);
        UpdateNodeAddressInStatusBar(active);
        UpdateTrayStatus();
    }

    private void UpdateNodeAddressInStatusBar(VmessProfile? profile) => ApplyNodeHeaderTexts(profile);

    private void ApplyNodeHeaderTexts(VmessProfile? profile)
    {
        if (NodeSummaryText is null || NodeEndpointText is null)
        {
            return;
        }

        if (profile is null)
        {
            NodeSummaryText.Text = "[VMess] -";
            NodeEndpointText.Text = string.Empty;
            ScheduleMainHeaderResponsiveLayout();
            return;
        }

        NodeSummaryText.Text = $"[{profile.ProtocolDisplay}] {profile.DisplayName}";
        NodeEndpointText.Text = profile.Endpoint;
        ScheduleMainHeaderResponsiveLayout();
    }

    private void ScheduleMainHeaderResponsiveLayout()
    {
        Dispatcher.BeginInvoke(UpdateMainHeaderResponsiveLayout, DispatcherPriority.Loaded);
    }

    private void MainHeaderBorder_SizeChanged(object sender, SizeChangedEventArgs e) =>
        ScheduleMainHeaderResponsiveLayout();

    private void UpdateMainHeaderResponsiveLayout()
    {
        if (!_isUiReady || NodeEndpointPanel is null || NodeSummaryText is null || NodeEndpointText is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(NodeEndpointText.Text))
        {
            NodeEndpointPanel.Visibility = Visibility.Collapsed;
            return;
        }

        var availableWidth = GetNodeInfoAvailableWidth();
        var requiredWidth = MeasureNodeInfoContentWidth(includeEndpoint: true);
        var showEndpoint = requiredWidth <= availableWidth;
        NodeEndpointPanel.Visibility = showEndpoint ? Visibility.Visible : Visibility.Collapsed;
    }

    private double GetNodeInfoAvailableWidth()
    {
        if (MainHeaderBorder is null)
        {
            return double.PositiveInfinity;
        }

        var headerWidth = MainHeaderBorder.ActualWidth;
        if (headerWidth <= 0)
        {
            return double.PositiveInfinity;
        }

        var proxyWidth = ProxyToggleBorder?.ActualWidth ?? 0;
        var trafficWidth = TrafficStatsPanel?.ActualWidth ?? 0;
        const double headerHorizontalPadding = 48;
        const double nodeInfoHorizontalMargin = 24;
        const double layoutBuffer = 12;

        return Math.Max(
            0,
            headerWidth - headerHorizontalPadding - proxyWidth - trafficWidth - nodeInfoHorizontalMargin - layoutBuffer);
    }

    private double MeasureNodeInfoContentWidth(bool includeEndpoint)
    {
        var width = MeasureTextBlockWidth(NodeSummaryText, NodeSummaryText.Text);
        if (includeEndpoint)
        {
            width += MeasureTextBlockWidth(NodeSummaryText, " · ");
            width += MeasureTextBlockWidth(NodeEndpointText, NodeEndpointText.Text);
        }

        width += MeasureTextBlockWidth(NodeSummaryText, " · ");
        width += Math.Max(MeasureTextBlockWidth(CurrentTcpLatencyText, CurrentTcpLatencyText.Text), 72);

        if (NodeAvailabilityTag is not null && NodeAvailabilityTag.Visibility == Visibility.Visible)
        {
            NodeAvailabilityTag.UpdateLayout();
            width += NodeAvailabilityTag.ActualWidth + NodeAvailabilityTag.Margin.Left + NodeAvailabilityTag.Margin.Right;
        }

        if (NodeInfoBorder is not null)
        {
            width += NodeInfoBorder.Padding.Left + NodeInfoBorder.Padding.Right;
            width += NodeInfoBorder.BorderThickness.Left + NodeInfoBorder.BorderThickness.Right;
        }

        return width;
    }

    private static double MeasureTextBlockWidth(TextBlock reference, string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var formattedText = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            System.Windows.FlowDirection.LeftToRight,
            new Typeface(reference.FontFamily, reference.FontStyle, reference.FontWeight, reference.FontStretch),
            reference.FontSize,
            reference.Foreground,
            VisualTreeHelper.GetDpi(reference).PixelsPerDip);

        return formattedText.WidthIncludingTrailingWhitespace;
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
            ? GreenBrush()
            : new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B));
        UpdateTrayStatus();
    }

    private void ApplyTheme()
    {
        var accent = ThemeService.ParseAccentColor(_settings.ThemeAccentColor);
        ThemeService.Apply(accent);
        ThemeService.ApplyToResources(Resources, accent);
        SyncThemeSettingsUi(accent);
    }

    private void SyncThemeSettingsUi(Color accent)
    {
        if (!_isUiReady || ThemeAccentPreview is null || ThemeAccentHexText is null)
        {
            return;
        }

        ThemeAccentPreview.Background = new SolidColorBrush(accent);
        ThemeAccentHexText.Text = ThemeService.FormatHex(accent);
    }

    private void ThemeColorPickButton_Click(object sender, RoutedEventArgs e)
    {
        var current = ThemeService.ParseAccentColor(_settings.ThemeAccentColor);
        using var dialog = new Forms.ColorDialog
        {
            FullOpen = true,
            Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B)
        };

        if (dialog.ShowDialog() != Forms.DialogResult.OK)
        {
            return;
        }

        _settings.ThemeAccentColor = ThemeService.FormatHex(
            Color.FromRgb(dialog.Color.R, dialog.Color.G, dialog.Color.B));
        ApplyTheme();
        _settingsStore.Save(_settings);
    }

    private void ThemeColorResetButton_Click(object sender, RoutedEventArgs e)
    {
        _settings.ThemeAccentColor = ThemeService.DefaultAccentHex;
        ApplyTheme();
        _settingsStore.Save(_settings);
    }

    private void ApplyThemeBackground()
    {
        var isCustom = IsCustomThemeBackgroundSource(_settings.ThemeBackgroundSource);
        BitmapImage? bitmap = null;

        if (isCustom)
        {
            var imagePath = ThemeBackgroundService.ResolveAbsolutePath(_settings.ThemeBackgroundImagePath);
            if (!string.IsNullOrWhiteSpace(imagePath))
            {
                bitmap = TryLoadBitmapFromFile(imagePath);
            }

            if (bitmap is null)
            {
                isCustom = false;
                _settings.ThemeBackgroundSource = "default";
                _settings.ThemeBackgroundImagePath = null;
                _settingsStore.Save(_settings);
            }
        }

        bitmap ??= TryLoadAppBitmap(DefaultChatBackgroundResource);

        if (AppThemeBackgroundImage is not null)
        {
            AppThemeBackgroundImage.Source = bitmap;
            AppThemeBackgroundImage.Visibility = Visibility.Visible;
        }

        if (AppContentRoot is not null)
        {
            AppContentRoot.Background = System.Windows.Media.Brushes.Transparent;
        }

        ApplyGlassPanelResources();
        ApplyGlassChrome();
        SyncThemeBackgroundSettingsUi(bitmap, isCustom);
    }

    private void SyncThemeBackgroundSettingsUi(BitmapImage? activeBitmap, bool isCustom)
    {
        if (!_isUiReady ||
            ThemeBackgroundStatusText is null ||
            ThemeBackgroundPreviewImage is null ||
            ThemeBackgroundResetButton is null)
        {
            return;
        }

        ThemeBackgroundPreviewImage.Source = activeBitmap;
        ThemeBackgroundPreviewImage.Visibility = Visibility.Visible;
        ThemeBackgroundResetButton.IsEnabled = isCustom;
        ThemeBackgroundStatusText.Text = isCustom ? "本地图片" : "默认";
    }

    private void ApplyGlassChrome()
    {
        var light = SystemThemeService.IsLightMode();
        if (SidebarBorder is not null)
        {
            SidebarBorder.Background = CreateSemiTransparentSidebarBrush(light);
        }

        if (MainHeaderBorder is not null)
        {
            MainHeaderBorder.Background = CreateFrozenBrush(
                light ? Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xE0, 0x25, 0x25, 0x26));
        }
    }

    private static bool IsCustomThemeBackgroundSource(string? source) =>
        string.Equals(source, "local", StringComparison.OrdinalIgnoreCase);

    private void ApplyGlassPanelResources()
    {
        var light = SystemThemeService.IsLightMode();
        Resources["PanelGlassBrush"] = CreateFrozenBrush(
            light ? Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xE0, 0x25, 0x25, 0x26));
        Resources["Panel2GlassBrush"] = CreateFrozenBrush(
            light ? Color.FromArgb(0xD2, 0xF8, 0xFA, 0xFC) : Color.FromArgb(0xD2, 0x2D, 0x2D, 0x30));
        Resources["Panel3GlassBrush"] = CreateFrozenBrush(
            light ? Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xE6, 0x30, 0x30, 0x32));
        Resources["RowAltGlassBrush"] = CreateFrozenBrush(
            light ? Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xB3, 0x25, 0x25, 0x26));
        Resources["RowHoverGlassBrush"] = CreateFrozenBrush(
            light ? Color.FromArgb(0xCC, 0xF8, 0xFA, 0xFC) : Color.FromArgb(0xCC, 0x3A, 0x3A, 0x3C));
        Resources["ChatPanelGlassBrush"] = CreateFrozenBrush(
            light ? Color.FromArgb(0x73, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x73, 0x25, 0x25, 0x28));
        Resources["ChatBubbleAdminBrush"] = CreateFrozenBrush(
            light ? Color.FromArgb(0x99, 0xEF, 0xF6, 0xFF) : Color.FromArgb(0x99, 0x1E, 0x3A, 0x5F));
        Resources["ChatBubbleUserBrush"] = CreateFrozenBrush(
            light ? Color.FromArgb(0xB3, 0x60, 0xA5, 0xFA) : Color.FromArgb(0xB3, 0x3B, 0x82, 0xF6));
    }

    private static LinearGradientBrush CreateSemiTransparentSidebarBrush(bool light)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new(0, 0),
            EndPoint = new(0, 1)
        };
        if (light)
        {
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0xBF, 0xFF, 0xFF, 0xFF), 0));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0xBF, 0xF8, 0xFA, 0xFC), 1));
        }
        else
        {
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0xBF, 0x25, 0x25, 0x26), 0));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0xBF, 0x2D, 0x2D, 0x30), 1));
        }

        brush.Freeze();
        return brush;
    }

    private static SolidColorBrush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private void ThemeBackgroundUploadButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择主题背景图片",
            Filter = "图片文件|*.jpg;*.jpeg;*.png;*.webp;*.bmp"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var storedPath = ThemeBackgroundService.ImportFromFile(dialog.FileName);
            _settings.ThemeBackgroundSource = "local";
            _settings.ThemeBackgroundImagePath = storedPath;
            _settingsStore.Save(_settings);
            ApplyThemeBackground();
        }
        catch (Exception ex)
        {
            ThemedMessageDialog.Show(
                this,
                "上传主题背景失败",
                [ex.Message],
                ThemedMessageKind.Error);
        }
    }

    private void ThemeBackgroundResetButton_Click(object sender, RoutedEventArgs e)
    {
        _settings.ThemeBackgroundSource = "default";
        _settings.ThemeBackgroundImagePath = null;
        _settingsStore.Save(_settings);
        ApplyThemeBackground();
    }

    private void ConfigureAuthService()
    {
        var apiBaseUrl = ApiDefaults.NormalizeAuthApiBaseUrl(_settings.AuthApiBaseUrl);
        if (!string.Equals(_settings.AuthApiBaseUrl, apiBaseUrl, StringComparison.Ordinal))
        {
            _settings.AuthApiBaseUrl = apiBaseUrl;
            _settingsStore.Save(_settings);
        }

        _authService.Configure(apiBaseUrl);
    }

    private void AuthService_AuthStateChanged()
    {
        Dispatcher.Invoke(() =>
        {
            UpdateAuthSidebar();
            SyncAuthRefreshTimer();
            UpdateNotificationPagesAuthState();
            _ = SyncBackendWebSocketAsync();
        });
    }

    private async Task SyncBackendWebSocketAsync()
    {
        if (_authService.IsAuthenticated)
        {
            UpdateChatConnectionStatus("connecting");
            await _backendWebSocket.ConnectAsync();
            if (_backendWebSocket.IsConnected)
            {
                _backendWebSocketReconnectTimer.Stop();
                if (!_backendWebSocketPingTimer.IsEnabled)
                {
                    _backendWebSocketPingTimer.Start();
                }
            }
            else if (!_backendWebSocketReconnectTimer.IsEnabled)
            {
                _backendWebSocketReconnectTimer.Start();
            }
        }
        else
        {
            _backendWebSocketPingTimer.Stop();
            _backendWebSocketReconnectTimer.Stop();
            await _backendWebSocket.DisconnectAsync();
            UpdateChatConnectionStatus("disconnected");
            _chatMessages.Clear();
            _chatMessageIds.Clear();
            _chatFileLocalPaths.Clear();
            if (ChatMessagesPanel is not null)
            {
                ChatMessagesPanel.Children.Clear();
            }

            UpdateChatEmptyState();
        }
    }

    private void BackendWebSocket_ConnectionStateChanged(string state)
    {
        Dispatcher.Invoke(() => UpdateChatConnectionStatus(state));
    }

    private void BackendWebSocket_Connected()
    {
        Dispatcher.Invoke(() => _ = RefreshUserProfileFromServerAsync());
    }

    private void BackendWebSocket_UserProfileUpdated()
    {
        Dispatcher.Invoke(() => _ = RefreshUserProfileFromServerAsync());
    }

    private void BackendWebSocket_ForceLogoutReceived(ForceLogoutPushMessage payload)
    {
        var message = payload;
        Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
        {
            _ = HandleForceLogoutAsync(message);
        }));
    }

    private void BackendWebSocket_TokenExpiredReceived(TokenExpiredPushMessage payload)
    {
        var message = payload;
        Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
        {
            _ = HandleTokenExpiredAsync(message);
        }));
    }

    private void BackendWebSocket_HeartbeatTimeoutReceived(HeartbeatTimeoutPushMessage payload)
    {
        var message = payload;
        Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
        {
            _ = HandleHeartbeatTimeoutAsync(message);
        }));
    }

    private async Task HandleForceLogoutAsync(ForceLogoutPushMessage payload)
    {
        if (_isHandlingForceLogout || _isHandlingTokenExpired)
        {
            return;
        }

        _isHandlingForceLogout = true;
        try
        {
            _backendWebSocketPingTimer.Stop();
            _backendWebSocketReconnectTimer.Stop();
            StopAuthRefreshTimer();

            _authService.ForceLogoutLocally();
            ClearCloudProfilesOnLogout();
            ClearAuthMessages();
            UpdateChatConnectionStatus("disconnected");
            ForceLogoutDialog.Show(this, payload);
            if (!payload.PreventsRelogin)
            {
                ShowLoginPage();
            }

            try
            {
                await _backendWebSocket.DisconnectAsync();
            }
            catch (Exception ex)
            {
                DiagnosticLogService.Warning($"Force logout disconnect failed: {ex.Message}");
            }
        }
        finally
        {
            _isHandlingForceLogout = false;
        }
    }

    private async Task HandleTokenExpiredAsync(TokenExpiredPushMessage payload)
    {
        if (_isHandlingTokenExpired || _isHandlingForceLogout)
        {
            return;
        }

        _isHandlingTokenExpired = true;
        try
        {
            _backendWebSocketPingTimer.Stop();
            _backendWebSocketReconnectTimer.Stop();

            DiagnosticLogService.Info(
                $"TOKEN_EXPIRED received. message={payload.GetDisplayMessage()} — attempting token refresh before logout.");

            try
            {
                await _backendWebSocket.DisconnectAsync();
            }
            catch (Exception ex)
            {
                DiagnosticLogService.Warning($"Token expired disconnect failed: {ex.Message}");
            }

            if (await _authService.RefreshSessionAsync())
            {
                DiagnosticLogService.Info("TOKEN_EXPIRED recovered via refresh token.");
                SyncAuthRefreshTimer();
                UpdateAuthSidebar();
                await SyncBackendWebSocketAsync();
                return;
            }

            DiagnosticLogService.Warning("TOKEN_EXPIRED refresh failed; clearing local session.");
            StopAuthRefreshTimer();
            _authService.ForceLogoutLocally();
            ClearCloudProfilesOnLogout();
            UpdateAuthSidebar();
            _chatMessages.Clear();
            _chatMessageIds.Clear();
            _chatFileLocalPaths.Clear();
            UpdateChatConnectionStatus("disconnected");
            ShowLoginPage();
            ShowAuthMessage(LoginMessageText, payload.GetDisplayMessage());
        }
        finally
        {
            _isHandlingTokenExpired = false;
        }
    }

    private async Task HandleHeartbeatTimeoutAsync(HeartbeatTimeoutPushMessage payload)
    {
        if (_isHandlingForceLogout || _isHandlingTokenExpired || !_authService.IsAuthenticated)
        {
            return;
        }

        _backendWebSocketPingTimer.Stop();
        _backendWebSocketReconnectTimer.Stop();
        UpdateChatConnectionStatus("connecting", payload.GetDisplayMessage());

        try
        {
            await _backendWebSocket.DisconnectAsync();
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Warning($"Heartbeat timeout disconnect failed: {ex.Message}");
        }

        if (!_authService.IsAuthenticated || _isHandlingForceLogout || _isHandlingTokenExpired)
        {
            return;
        }

        await SyncBackendWebSocketAsync();
    }

    private async Task RefreshUserProfileFromServerAsync()
    {
        if (!_authService.IsAuthenticated)
        {
            return;
        }

        await _authService.RefreshProfileFromServerAsync();
        Dispatcher.Invoke(UpdateAuthSidebar);
    }

    private void BackendWebSocket_Disconnected()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
        {
            UpdateChatConnectionStatus("disconnected");
            if (_isHandlingForceLogout || _isHandlingTokenExpired || !_authService.IsAuthenticated || _backendWebSocketReconnectTimer.IsEnabled)
            {
                return;
            }

            _backendWebSocketReconnectTimer.Start();
        }));
    }

    private async void BackendWebSocketReconnectTimer_Tick(object? sender, EventArgs e)
    {
        if (!_authService.IsAuthenticated || _backendWebSocket.IsConnected)
        {
            _backendWebSocketReconnectTimer.Stop();
            return;
        }

        await SyncBackendWebSocketAsync();
    }

    private void UpdateChatConnectionStatus(string state, string? statusMessage = null)
    {
    }

    private async void BackendWebSocketPingTimer_Tick(object? sender, EventArgs e)
    {
        await _backendWebSocket.SendPingAsync();
        if (!_backendWebSocket.IsConnected && _authService.IsAuthenticated && !_backendWebSocketReconnectTimer.IsEnabled)
        {
            _backendWebSocketReconnectTimer.Start();
        }
    }

    private void BackendWebSocket_ChatMessageReceived(ChatMessage message)
    {
        Dispatcher.Invoke(() =>
        {
            var isNew = !_chatMessageIds.Contains(message.Id);
            AppendChatMessage(message);
            if (!isNew)
            {
                return;
            }

            if (message.IsFromAdmin)
            {
                if (ContactAdminPageScroll.Visibility != Visibility.Visible)
                {
                    IncrementUnreadAdminChatCount();
                }

                ShowChatMessageToast(message);
            }

            if (ContactAdminChatPanel.Visibility == Visibility.Visible)
            {
                ScrollChatToEnd();
            }
        });
    }

    private void BackendWebSocket_BroadcastReceived(BroadcastPushMessage message)
    {
        Dispatcher.Invoke(() =>
        {
            if (AnnouncementPageScroll.Visibility == Visibility.Visible && _authService.IsAuthenticated)
            {
                var detailTitle = AnnouncementDetailView.Visibility == Visibility.Visible
                    ? AnnouncementDetailTitleText.Text
                    : null;
                _ = LoadAnnouncementsAsync(detailTitle);
            }
        });
    }

    private void BackendWebSocket_VersionUpdateReceived(VersionUpdatePushMessage message)
    {
        Dispatcher.Invoke(() =>
        {
            if (!message.HasWindows)
            {
                return;
            }

            _ = HandleVersionUpdatePushAsync(message);
        });
    }

    private async Task HandleVersionUpdatePushAsync(VersionUpdatePushMessage message)
    {
        await RefreshLatestVersionAsync(showLoadingStatus: false, triggerAutoDownload: true);
        if (VersionUpdatePageScroll.Visibility == Visibility.Visible)
        {
            return;
        }

        if (_cachedLatestRelease is not null && IsAppUpdateAvailable(_cachedLatestRelease))
        {
            SetVersionUpdateStatus($"收到新版本推送：v{message.VersionName}");
        }
    }

    private void SyncAuthRefreshTimer()
    {
        if (_authService.HasPersistedSession)
        {
            StartAuthRefreshTimer();
        }
        else
        {
            StopAuthRefreshTimer();
        }
    }

    private void StartAuthRefreshTimer()
    {
        if (!_authRefreshTimer.IsEnabled)
        {
            _authRefreshTimer.Start();
        }
    }

    private void StopAuthRefreshTimer()
    {
        _authRefreshTimer.Stop();
        _authRefreshInProgress = false;
    }

    private async void AuthRefreshTimer_Tick(object? sender, EventArgs e)
    {
        if (_authRefreshInProgress || !_authService.IsConfigured)
        {
            return;
        }

        _authRefreshInProgress = true;
        try
        {
            if (!await _authService.RefreshSessionAsync())
            {
                SyncAuthRefreshTimer();
            }
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Warning($"Background auth refresh failed: {ex.Message}");
        }
        finally
        {
            _authRefreshInProgress = false;
        }
    }

    private void UpdateAuthSidebar()
    {
        if (!_isUiReady || AuthGuestPanel is null || AuthUserPanel is null)
        {
            return;
        }

        if (_authService.HasPersistedSession)
        {
            AuthGuestPanel.Visibility = Visibility.Collapsed;
            AuthUserPanel.Visibility = Visibility.Visible;
            var nickname = _authService.CurrentNickname;
            var email = _authService.CurrentEmail ?? "";
            SideAuthNicknameText.Text = !string.IsNullOrWhiteSpace(nickname) ? nickname : "已登录";
            SideAuthEmailText.Text = email;
            SetSideAuthAvatar(AvatarUrlHelper.ResolveUserAvatarUrl(_authService.CurrentAvatarUrl));

            if (SideMenuEditProfile is not null)
            {
                SideMenuEditProfile.Visibility = Visibility.Visible;
            }

            if (SideMenuLogout is not null)
            {
                SideMenuLogout.Visibility = Visibility.Visible;
            }

            if (SideMenuLogin is not null)
            {
                SideMenuLogin.Visibility = Visibility.Collapsed;
            }

            if (SyncCloudSubscriptionsButton is not null)
            {
                SyncCloudSubscriptionsButton.Visibility = Visibility.Visible;
            }
        }
        else
        {
            AuthGuestPanel.Visibility = Visibility.Visible;
            AuthUserPanel.Visibility = Visibility.Collapsed;

            if (SideMenuEditProfile is not null)
            {
                SideMenuEditProfile.Visibility = Visibility.Collapsed;
            }

            if (SideMenuLogout is not null)
            {
                SideMenuLogout.Visibility = Visibility.Collapsed;
            }

            if (SideMenuLogin is not null)
            {
                SideMenuLogin.Visibility = Visibility.Visible;
            }

            if (SyncCloudSubscriptionsButton is not null)
            {
                SyncCloudSubscriptionsButton.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void SideUserArea_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element || SideUserContextMenu is null)
        {
            return;
        }

        if (!_authService.HasPersistedSession)
        {
            ShowLoginPage();
            e.Handled = true;
            return;
        }

        SideUserContextMenu.PlacementTarget = element;
        SideUserContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        SideUserContextMenu.IsOpen = true;
        e.Handled = true;
    }

    private void SetSideAuthAvatar(string avatarUrl)
    {
        _ = LoadSideAuthAvatarAsync(avatarUrl);
    }

    private async Task LoadSideAuthAvatarAsync(string avatarUrl)
    {
        if (SideAuthAvatarImage is null || SideAuthAvatarPlaceholder is null)
        {
            return;
        }

        var loadVersion = Interlocked.Increment(ref _sideAvatarLoadVersion);
        BitmapImage? bitmap = null;

        for (var attempt = 0; attempt < 3 && bitmap is null; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(400 * attempt));
            }

            if (loadVersion != _sideAvatarLoadVersion)
            {
                return;
            }

            bitmap = await AvatarImageLoader.LoadWithFallbackAsync(
                avatarUrl,
                AvatarUrlHelper.DefaultUserAvatarUrl);
        }

        if (loadVersion != _sideAvatarLoadVersion)
        {
            return;
        }

        if (bitmap is null)
        {
            ApplySideAuthAvatarState(null);
            DiagnosticLogService.Warning($"Side auth avatar failed to load: {avatarUrl}");
            return;
        }

        ApplySideAuthAvatarState(bitmap);
    }

    private void ApplySideAuthAvatarState(BitmapImage? bitmap)
    {
        if (SideAuthAvatarImage is null || SideAuthAvatarPlaceholder is null)
        {
            return;
        }

        SideAuthAvatarImage.ImageFailed -= SideAuthAvatarImage_ImageFailed;

        if (bitmap is null)
        {
            SideAuthAvatarImage.Source = null;
            SideAuthAvatarImage.Visibility = Visibility.Collapsed;
            SideAuthAvatarPlaceholder.Visibility = Visibility.Visible;
            return;
        }

        SideAuthAvatarImage.Source = bitmap;
        SideAuthAvatarImage.Visibility = Visibility.Visible;
        SideAuthAvatarPlaceholder.Visibility = Visibility.Collapsed;
        SideAuthAvatarImage.ImageFailed += SideAuthAvatarImage_ImageFailed;
    }

    private void SideAuthAvatarImage_ImageFailed(object? sender, ExceptionRoutedEventArgs e)
    {
        DiagnosticLogService.Warning($"Side auth avatar render failed: {e.ErrorException.Message}");
        Dispatcher.BeginInvoke(() =>
        {
            if (_authService.HasPersistedSession)
            {
                SetSideAuthAvatar(AvatarUrlHelper.ResolveUserAvatarUrl(_authService.CurrentAvatarUrl));
            }
            else
            {
                ApplySideAuthAvatarState(null);
            }
        }, DispatcherPriority.Background);
    }

    private static bool TryLoadAvatarBitmap(string avatarUrl, out BitmapImage? bitmap) =>
        AvatarImageLoader.TryLoadLocal(avatarUrl, out bitmap);

    private async void SideEditProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_authService.HasPersistedSession)
        {
            ShowLoginPage();
            return;
        }

        if (!await _authService.TryRestoreSessionAsync())
        {
            MessageBox.Show("登录已过期，请重新登录。", "Nexora", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new Dialogs.ProfileEditDialog(
            _authService,
            _authService.CurrentEmail ?? "",
            _authService.CurrentNickname,
            _authService.CurrentAvatarUrl)
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        if (SideMenuEditProfile is not null)
        {
            SideMenuEditProfile.IsEnabled = false;
        }

        try
        {
            var result = await _authService.UpdateProfileAsync(
                dialog.ShouldUpdateNickname,
                dialog.Nickname,
                dialog.SelectedAvatarFilePath);
            if (!result.Success)
            {
                var errorMessage = string.IsNullOrWhiteSpace(result.Message) ? "资料更新失败。" : result.Message;
                MessageBox.Show(errorMessage, "编辑资料", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            UpdateAuthSidebar();
            MessageBox.Show("资料已更新成功。", "编辑资料", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"资料更新失败：{ex.Message}", "Nexora", MessageBoxButton.OK, MessageBoxImage.Error);
            DiagnosticLogService.Error("Profile edit failed.", ex);
        }
        finally
        {
            if (SideMenuEditProfile is not null)
            {
                SideMenuEditProfile.IsEnabled = true;
            }
        }
    }

    private void SideLoginButton_Click(object sender, RoutedEventArgs e) => ShowLoginPage();

    private async void SideLogoutButton_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(
                "确定退出登录吗？退出后将清除本机云端节点与订阅缓存。",
                "退出登录",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        await _authService.LogoutAsync();
        ClearCloudProfilesOnLogout();
        ClearAuthMessages();
    }

    private async void SyncCloudSubscriptionsButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_authService.HasPersistedSession)
        {
            ThemedMessageDialog.Show(this, "请先登录后再从云端更新节点。");
            return;
        }

        if (!await _authService.TryRestoreSessionAsync())
        {
            ThemedMessageDialog.Show(this, "登录已过期，请重新登录后再更新订阅。", kind: ThemedMessageKind.Warning);
            return;
        }

        SyncCloudSubscriptionsButton.IsEnabled = false;
        try
        {
            await ReloadCloudSubscriptionsAsync(manualRefresh: true);
        }
        catch (Exception ex)
        {
            ThemedMessageDialog.Show(
                this,
                $"云端更新失败：{ex.Message}",
                kind: ThemedMessageKind.Warning);
            DiagnosticLogService.Error("Cloud subscription refresh failed.", ex);
        }
        finally
        {
            SyncCloudSubscriptionsButton.IsEnabled = true;
        }
    }

    private void ShowCloudSubscriptionReloadSummary(
        int subscriptionCount,
        int parsedSuccessCount,
        int invalidCount,
        int loadedNodeCount,
        bool isManual)
    {
        var headline = isManual ? "云端更新完成。" : "云端自动更新失败。";
        var details = new[]
        {
            $"获取订阅：{subscriptionCount}",
            $"解析成功：{parsedSuccessCount}",
            $"失效：{invalidCount}",
            $"加载并测速节点：{loadedNodeCount}"
        };

        if (isManual)
        {
            ThemedMessageDialog.Show(this, headline, details);
            return;
        }

        MessageBox.Show(
            string.Join(Environment.NewLine, new[] { headline, string.Empty }.Concat(details)),
            "Nexora",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private static void ShowCloudSubscriptionFailureMessage(string message)
    {
        MessageBox.Show(message, "Nexora", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void GoToRegisterPageButton_Click(object sender, RoutedEventArgs e) => ShowRegisterPage();

    private void GoToForgotPasswordPageButton_Click(object sender, RoutedEventArgs e) => ShowForgotPasswordPage();

    private void GoToLoginPageButton_Click(object sender, RoutedEventArgs e) => ShowLoginPage();

    private void HideAllAuthPages()
    {
        LoginPageScroll.Visibility = Visibility.Collapsed;
        RegisterPageScroll.Visibility = Visibility.Collapsed;
        ForgotPasswordPageScroll.Visibility = Visibility.Collapsed;
    }

    private void ShowLoginPage()
    {
        ClearAuthMessages();
        AuthDialogOverlay.Visibility = Visibility.Visible;
        HideAllAuthPages();
        LoginPageScroll.Visibility = Visibility.Visible;
        LoginEmailBox.Focus();
    }

    private void ShowRegisterPage()
    {
        ClearAuthMessages();
        AuthDialogOverlay.Visibility = Visibility.Visible;
        HideAllAuthPages();
        RegisterPageScroll.Visibility = Visibility.Visible;
        RegisterEmailBox.Focus();
    }

    private void ShowForgotPasswordPage()
    {
        ClearAuthMessages();
        AuthDialogOverlay.Visibility = Visibility.Visible;
        HideAllAuthPages();
        ForgotPasswordPageScroll.Visibility = Visibility.Visible;
        if (string.IsNullOrWhiteSpace(ForgotPasswordEmailBox.Text) &&
            !string.IsNullOrWhiteSpace(LoginEmailBox.Text))
        {
            ForgotPasswordEmailBox.Text = LoginEmailBox.Text.Trim();
        }

        ForgotPasswordEmailBox.Focus();
    }

    private void CloseAuthDialog()
    {
        AuthDialogOverlay.Visibility = Visibility.Collapsed;
        HideAllAuthPages();
    }

    private void CloseAuthDialogButton_Click(object sender, RoutedEventArgs e) => CloseAuthDialog();

    private void AuthDialogBackdrop_MouseDown(object sender, MouseButtonEventArgs e) => CloseAuthDialog();

    private void ClearAuthMessages()
    {
        LoginMessageText.Text = "";
        LoginMessageText.Visibility = Visibility.Collapsed;
        RegisterMessageText.Text = "";
        RegisterMessageText.Visibility = Visibility.Collapsed;
        ForgotPasswordMessageText.Text = "";
        ForgotPasswordMessageText.Visibility = Visibility.Collapsed;
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
            if (await ReloadCloudSubscriptionsAsync(manualRefresh: false))
            {
                await TryAutoStartProxyAfterLoginAsync();
            }

            await CheckAppUpdateOnStartupAsync();
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

    private async void SendForgotPasswordCodeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_forgotPasswordCodeCooldownSeconds > 0)
        {
            return;
        }

        SendForgotPasswordCodeButton.IsEnabled = false;
        SendForgotPasswordCodeButton.Content = "发送中…";
        try
        {
            var result = await _authService.SendResetPasswordCodeAsync(ForgotPasswordEmailBox.Text);
            if (!result.Success)
            {
                ShowAuthMessage(ForgotPasswordMessageText, result.Message);
                return;
            }

            ShowAuthMessage(ForgotPasswordMessageText, result.Message, isSuccess: true);
            StartForgotPasswordCodeCooldown();
        }
        finally
        {
            if (_forgotPasswordCodeCooldownSeconds <= 0)
            {
                SendForgotPasswordCodeButton.IsEnabled = true;
                SendForgotPasswordCodeButton.Content = "发送验证码";
            }
        }
    }

    private void StartForgotPasswordCodeCooldown()
    {
        _forgotPasswordCodeCooldownSeconds = 60;
        SendForgotPasswordCodeButton.IsEnabled = false;
        SendForgotPasswordCodeButton.Content = $"{_forgotPasswordCodeCooldownSeconds}s";
        _forgotPasswordCodeCooldownTimer.Start();
    }

    private void ForgotPasswordCodeCooldownTimer_Tick(object? sender, EventArgs e)
    {
        _forgotPasswordCodeCooldownSeconds--;
        if (_forgotPasswordCodeCooldownSeconds > 0)
        {
            SendForgotPasswordCodeButton.Content = $"{_forgotPasswordCodeCooldownSeconds}s";
            return;
        }

        _forgotPasswordCodeCooldownTimer.Stop();
        SendForgotPasswordCodeButton.IsEnabled = true;
        SendForgotPasswordCodeButton.Content = "发送验证码";
    }

    private async void ForgotPasswordSubmitButton_Click(object sender, RoutedEventArgs e)
    {
        ForgotPasswordSubmitButton.IsEnabled = false;
        ForgotPasswordSubmitButton.Content = "提交中…";
        try
        {
            var result = await _authService.ResetPasswordAsync(
                ForgotPasswordEmailBox.Text,
                ForgotPasswordCodeBox.Text,
                ForgotPasswordNewPasswordBox.Password,
                ForgotPasswordConfirmPasswordBox.Password);
            if (!result.Success)
            {
                ShowAuthMessage(ForgotPasswordMessageText, result.Message);
                return;
            }

            var email = ForgotPasswordEmailBox.Text.Trim();
            ForgotPasswordCodeBox.Clear();
            ForgotPasswordNewPasswordBox.Clear();
            ForgotPasswordConfirmPasswordBox.Clear();
            LoginEmailBox.Text = email;
            LoginPasswordBox.Clear();
            ShowLoginPage();
            ShowAuthMessage(
                LoginMessageText,
                string.IsNullOrWhiteSpace(result.Message) ? "密码已重置成功，请使用新密码登录。" : result.Message,
                isSuccess: true);
        }
        finally
        {
            ForgotPasswordSubmitButton.IsEnabled = true;
            ForgotPasswordSubmitButton.Content = "确认重置";
        }
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
            if (await ReloadCloudSubscriptionsAsync(manualRefresh: false))
            {
                await TryAutoStartProxyAfterLoginAsync();
            }

            await CheckAppUpdateOnStartupAsync();
            ShowNodePage();
        }
        finally
        {
            RegisterSubmitButton.IsEnabled = true;
            RegisterSubmitButton.Content = "注册";
        }
    }

    private async Task<bool> ReloadCloudSubscriptionsAsync(bool manualRefresh)
    {
        if (!await _authService.TryRestoreSessionAsync())
        {
            if (manualRefresh)
            {
                ThemedMessageDialog.Show(this, "登录已过期，请重新登录。", kind: ThemedMessageKind.Warning);
            }
            else
            {
                ShowCloudSubscriptionFailureMessage("登录已过期，请重新登录。");
            }

            return false;
        }

        var fetchResult = await _authService.SubscriptionSync.FetchLoginSubscriptionsAsync();
        if (!fetchResult.RetrievedFromServer)
        {
            if (fetchResult.IsTransientFailure || fetchResult.Subscriptions.Count == 0)
            {
                var message = string.IsNullOrWhiteSpace(fetchResult.ErrorMessage)
                    ? "无法连接云端，已保留本地节点。"
                    : $"无法连接云端，已保留本地节点：{fetchResult.ErrorMessage}";
                if (manualRefresh)
                {
                    ThemedMessageDialog.Show(this, message, kind: ThemedMessageKind.Warning);
                }
                else
                {
                    ShowCloudSubscriptionFailureMessage(message);
                }

                return false;
            }
        }

        var subscriptions = fetchResult.Subscriptions;
        if (fetchResult.RetrievedFromServer)
        {
            PruneStaleCloudSubscriptions(subscriptions);
        }

        foreach (var subscription in subscriptions)
        {
            RegisterServerSubscriptionSource(subscription, isInvalid: false);
        }

        _settingsStore.Save(_settings);
        RefreshSubscriptionFilterOptions();
        RestoreSubscriptionAutoRefreshTimers();

        if (subscriptions.Count == 0)
        {
            var nextActiveId = _profiles.FirstOrDefault(profile => profile.Id == _settings.SelectedProfileId)?.Id
                ?? _profiles.FirstOrDefault()?.Id;
            SaveProfiles(nextActiveId);
            RefreshNodePicker();
            SyncNodePickerDisplay();
            ProfilesGrid.Items.Refresh();
            _settingsStore.Save(_settings);
            RefreshSubscriptionFilterOptions();
            if (manualRefresh)
            {
                ThemedMessageDialog.Show(this, "当前账号暂无云端订阅。");
            }

            return false;
        }

        var items = await _authService.SubscriptionSync.LoadLoginSubscriptionsAsync(subscriptions);
        var loadedProfiles = new List<VmessProfile>();
        var syncedSubscriptions = 0;
        var profileCollectionChanged = false;

        foreach (var item in items)
        {
            if (!item.Success || item.ImportResult is null)
            {
                if (ShouldTreatRefreshFailureAsTrafficExhausted(
                        new SubscriptionGroupIdentity(item.Subscription.Name, IsLocal: false, IsManual: false),
                        item.ErrorMessage))
                {
                    SetSubscriptionTrafficExhausted(item.Subscription.Name);
                    DiagnosticLogService.Warning(
                        $"Cloud subscription \"{item.Subscription.Name}\" traffic exhausted; kept existing nodes and paused auto refresh.");
                }
                else if (SubscriptionTrafficHelper.IsTransientNetworkError(item.ErrorMessage))
                {
                    DiagnosticLogService.Warning(
                        $"Cloud subscription \"{item.Subscription.Name}\" fetch failed (transient): {item.ErrorMessage ?? "network error"}; kept existing nodes.");
                }
                else
                {
                    MarkServerSubscriptionInvalid(item.Subscription);
                    RemoveProfilesForSubscription(new SubscriptionGroupIdentity(
                        item.Subscription.Name,
                        IsLocal: false,
                        IsManual: false));
                    profileCollectionChanged = true;
                    DiagnosticLogService.Warning(
                        $"Cloud subscription \"{item.Subscription.Name}\" marked invalid: {item.ErrorMessage ?? "no profiles parsed"}");
                }

                continue;
            }

            var source = RegisterServerSubscriptionSource(item.Subscription, isInvalid: false);
            var subscriptionName = item.Subscription.Name;
            var importResult = item.ImportResult;

            if (SubscriptionTrafficHelper.IsTrafficExhausted(importResult.TrafficInfo) ||
                SubscriptionTrafficHelper.AreProfilesTrafficExhausted(importResult.Profiles))
            {
                SetSubscriptionTrafficExhausted(subscriptionName, save: false);
                ApplySubscriptionTrafficInfo(importResult.TrafficInfo);
                DiagnosticLogService.Warning(
                    $"Cloud subscription \"{subscriptionName}\" traffic exhausted after import; kept existing nodes and paused auto refresh.");
                continue;
            }

            RemoveProfilesForSubscription(new SubscriptionGroupIdentity(subscriptionName, IsLocal: false, IsManual: false));
            profileCollectionChanged = true;

            SubscriptionMetadataHelper.ApplyToProfiles(importResult, subscriptionName);

            var syncResult = await _authService.SubscriptionSync.SyncRefreshAsync(
                importResult,
                source,
                subscriptionName);

            if (!syncResult.Success)
            {
                DiagnosticLogService.Warning($"Subscription sync failed for \"{subscriptionName}\": {syncResult.Message}");
            }
            else
            {
                syncedSubscriptions++;
            }

            foreach (var profile in importResult.Profiles)
            {
                MarkProfileAsCloudManaged(profile);
                _profiles.Add(profile);
                loadedProfiles.Add(profile);
            }

            ApplySubscriptionTrafficInfo(importResult.TrafficInfo);
        }

        _settingsStore.Save(_settings);
        var invalidSubscriptions = items.Count(item => !item.Success || item.ImportResult is null);
        var parsedSuccessCount = items.Count - invalidSubscriptions;
        if (loadedProfiles.Count > 0)
        {
            SaveProfiles(PreserveActiveProfileId());
            RefreshNodePicker();
            RefreshProfilesView();
            RefreshRegionFilterOptions();
            RefreshSubscriptionFilterOptions();
            RestoreSubscriptionAutoRefreshTimers();
            ScheduleRegionEnrichment(loadedProfiles);
            await RunTcpLatencyTestsAsync(loadedProfiles, parallel: true);
            ApplyActiveProfileSelection(autoSelectIfMissing: true, save: true);
        }
        else
        {
            if (profileCollectionChanged)
            {
                ApplyActiveProfileSelection(autoSelectIfMissing: true, save: true);
            }

            RefreshNodePicker();
            RefreshProfilesView();
            RefreshRegionFilterOptions();
            RefreshSubscriptionFilterOptions();
            RestoreSubscriptionAutoRefreshTimers();
        }

        if (manualRefresh)
        {
            ShowCloudSubscriptionReloadSummary(
                subscriptions.Count,
                parsedSuccessCount,
                invalidSubscriptions,
                loadedProfiles.Count,
                isManual: true);
        }
        else if (subscriptions.Count > 0 && loadedProfiles.Count == 0)
        {
            ShowCloudSubscriptionReloadSummary(
                subscriptions.Count,
                parsedSuccessCount,
                invalidSubscriptions,
                loadedProfiles.Count,
                isManual: false);
        }

        return loadedProfiles.Count > 0;
    }

    private async Task TryAutoStartProxyAfterLoginAsync()
    {
        if (_profiles.Count == 0 || _coreService.IsRunning)
        {
            return;
        }

        try
        {
            await StartProxyAsync();
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Warning($"Auto-start proxy after login failed: {ex.Message}");
        }
    }

    private void ClearCloudProfilesOnLogout()
    {
        var removed = _profiles.Where(ShouldRemoveOnLogout).ToList();
        foreach (var profile in removed)
        {
            _profiles.Remove(profile);
        }

        var cloudSourceKeys = _settings.SubscriptionSources
            .Where(pair => pair.Value.ServerSubscriptionId is > 0 && !pair.Value.IsLocalOnly)
            .Select(pair => pair.Key)
            .ToList();

        foreach (var key in cloudSourceKeys)
        {
            _settings.SubscriptionSources.Remove(key);
        }

        var selectedId = _profiles.FirstOrDefault(profile => profile.Id == _settings.SelectedProfileId)?.Id
            ?? _profiles.FirstOrDefault()?.Id;
        _settings.SelectedProfileId = selectedId;
        SaveProfiles(selectedId);
        RefreshNodePicker();
        SyncNodePickerDisplay();
        RefreshRegionFilterOptions();
        RefreshSubscriptionFilterOptions();
        ProfilesGrid.Items.Refresh();
        RestoreSubscriptionAutoRefreshTimers();
        _settingsStore.Save(_settings);
    }

    private static bool ShouldRemoveOnLogout(VmessProfile profile) => profile.IsCloudManaged;

    private static void MarkProfileAsCloudManaged(VmessProfile profile)
    {
        profile.IsCloudManaged = true;
        profile.IsLocalManual = false;
        profile.IsLocalSubscription = false;
    }

    private static void MarkProfileAsLocalManual(VmessProfile profile)
    {
        profile.IsCloudManaged = false;
        profile.IsLocalManual = true;
        profile.IsLocalSubscription = false;
        profile.SubscriptionName = "";
    }

    private static void MarkProfileAsLocalSubscription(VmessProfile profile)
    {
        profile.IsCloudManaged = false;
        profile.IsLocalManual = false;
        profile.IsLocalSubscription = true;
    }

    private static bool MigrateLocalProfileFlags(VmessProfile profile, AppSettings settings)
    {
        if (profile.IsLocalManual)
        {
            if (profile.IsCloudManaged || profile.IsLocalSubscription || !string.IsNullOrWhiteSpace(profile.SubscriptionName))
            {
                profile.IsCloudManaged = false;
                profile.IsLocalSubscription = false;
                profile.SubscriptionName = "";
                return true;
            }

            return false;
        }

        if (profile.IsLocalSubscription)
        {
            if (profile.IsCloudManaged || profile.IsLocalManual)
            {
                profile.IsCloudManaged = false;
                profile.IsLocalManual = false;
                return true;
            }

            return false;
        }

        if (profile.IsCloudManaged)
        {
            if (profile.IsLocalManual || profile.IsLocalSubscription)
            {
                profile.IsLocalManual = false;
                profile.IsLocalSubscription = false;
                return true;
            }

            return false;
        }

        if (string.IsNullOrWhiteSpace(profile.SubscriptionName))
        {
            profile.IsLocalManual = true;
            profile.IsLocalSubscription = false;
            profile.IsCloudManaged = false;
            return true;
        }

        var localSourceKey = LocalSubscriptionHelper.GetLocalSourceKey(profile.SubscriptionName);
        if (settings.SubscriptionSources.ContainsKey(localSourceKey))
        {
            profile.IsLocalSubscription = true;
            profile.IsLocalManual = false;
            profile.IsCloudManaged = false;
            return true;
        }

        if (settings.SubscriptionSources.TryGetValue(profile.SubscriptionName, out var cloudSource) &&
            !cloudSource.IsLocalOnly &&
            cloudSource.ServerSubscriptionId is > 0)
        {
            profile.IsCloudManaged = true;
            profile.IsLocalManual = false;
            profile.IsLocalSubscription = false;
            return true;
        }

        profile.IsLocalSubscription = true;
        profile.IsLocalManual = false;
        profile.IsCloudManaged = false;
        return true;
    }

    private static bool MigrateLocalSubscriptionSource(SubscriptionSource source)
    {
        if (source.IsLocalOnly || source.ServerSubscriptionId is > 0)
        {
            return false;
        }

        source.IsLocalOnly = true;
        return true;
    }

    private static string GetSubscriptionFilterDisplayName(string sourceKey, SubscriptionSource? source)
    {
        if (LocalSubscriptionHelper.IsLocalManualGroupKey(sourceKey))
        {
            return LocalSubscriptionHelper.LocalLabel;
        }

        var subscriptionName = LocalSubscriptionHelper.GetSourceKeySubscriptionName(sourceKey);
        if (LocalSubscriptionHelper.IsLocalSourceKey(sourceKey) || source?.IsLocalOnly == true)
        {
            var name = string.IsNullOrWhiteSpace(source?.DisplayName) ? subscriptionName : source.DisplayName!;
            return LocalSubscriptionHelper.FormatLocalSubscriptionDisplay(name);
        }

        var cloudName = string.IsNullOrWhiteSpace(source?.DisplayName) ? subscriptionName : source.DisplayName!;
        return cloudName;
    }

    private static bool ProfileMatchesSubscriptionFilter(VmessProfile profile, string filterKey)
    {
        if (string.Equals(filterKey, LocalSubscriptionHelper.LocalLabel, StringComparison.OrdinalIgnoreCase))
        {
            return profile.IsLocalManual;
        }

        if (LocalSubscriptionHelper.IsLocalSourceKey(filterKey))
        {
            var subscriptionName = LocalSubscriptionHelper.GetSourceKeySubscriptionName(filterKey);
            return profile.IsLocalSubscription &&
                   string.Equals(profile.SubscriptionName, subscriptionName, StringComparison.OrdinalIgnoreCase);
        }

        return profile.IsCloudManaged &&
               string.Equals(profile.SubscriptionName, filterKey, StringComparison.OrdinalIgnoreCase);
    }

    private void RemoveProfilesForSubscription(SubscriptionGroupIdentity scope)
    {
        var removed = _profiles
            .Where(profile => LocalSubscriptionHelper.ProfileMatchesScope(profile, scope))
            .ToList();

        foreach (var profile in removed)
        {
            _profiles.Remove(profile);
        }
    }

    private static SubscriptionGroupIdentity IdentityFromSourceKey(string sourceKey)
    {
        if (LocalSubscriptionHelper.IsLocalSourceKey(sourceKey))
        {
            return new SubscriptionGroupIdentity(
                LocalSubscriptionHelper.GetSourceKeySubscriptionName(sourceKey),
                IsLocal: true,
                IsManual: false);
        }

        return new SubscriptionGroupIdentity(sourceKey, IsLocal: false, IsManual: false);
    }

    private void MigrateLocalSubscriptionSourceKeys(ref bool metadataChanged)
    {
        var toMigrate = _settings.SubscriptionSources
            .Where(pair => pair.Value.IsLocalOnly && !LocalSubscriptionHelper.IsLocalSourceKey(pair.Key))
            .ToList();

        foreach (var (key, source) in toMigrate)
        {
            var newKey = LocalSubscriptionHelper.GetLocalSourceKey(key);
            if (!_settings.SubscriptionSources.ContainsKey(newKey))
            {
                _settings.SubscriptionSources[newKey] = source;
            }

            _settings.SubscriptionSources.Remove(key);
            metadataChanged = true;
        }
    }

    private void PruneStaleCloudSubscriptions(IReadOnlyList<ServerSubscription> currentSubscriptions)
    {
        var validIds = currentSubscriptions
            .Where(subscription => subscription.Id > 0)
            .Select(subscription => subscription.Id)
            .ToHashSet();
        var validUrls = currentSubscriptions
            .Select(subscription => subscription.Url)
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var validNames = currentSubscriptions
            .Select(subscription => subscription.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var keysToRemove = new List<string>();
        foreach (var (subscriptionName, source) in _settings.SubscriptionSources)
        {
            if (string.Equals(subscriptionName, LocalSubscriptionHelper.LocalLabel, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (source.IsLocalOnly)
            {
                continue;
            }

            var isCloudSource = source.ServerSubscriptionId is > 0 ||
                                _profiles.Any(profile =>
                                    profile.IsCloudManaged &&
                                    string.Equals(profile.SubscriptionName, subscriptionName, StringComparison.OrdinalIgnoreCase));
            if (!isCloudSource)
            {
                continue;
            }

            var displayName = RemoveInvalidSubscriptionSuffix(source.DisplayName ?? subscriptionName);
            var matchesServer =
                (source.ServerSubscriptionId is int serverId && serverId > 0 && validIds.Contains(serverId)) ||
                (!string.IsNullOrWhiteSpace(source.Url) && validUrls.Contains(source.Url)) ||
                validNames.Contains(subscriptionName) ||
                validNames.Contains(displayName);

            if (!matchesServer)
            {
                keysToRemove.Add(subscriptionName);
            }
        }

        if (keysToRemove.Count == 0)
        {
            return;
        }

        foreach (var subscriptionName in keysToRemove)
        {
            StopSubscriptionAutoRefresh(subscriptionName, save: false);
            _settings.SubscriptionSources.Remove(subscriptionName);
            RemoveProfilesForSubscription(new SubscriptionGroupIdentity(subscriptionName, IsLocal: false, IsManual: false));
            DiagnosticLogService.Info($"Removed stale cloud subscription \"{subscriptionName}\" from local storage.");
        }
    }

    private List<VmessProfile> GetProfilesForSubscription(SubscriptionGroupIdentity scope) =>
        _profiles
            .Where(profile => LocalSubscriptionHelper.ProfileMatchesScope(profile, scope))
            .ToList();

    private bool IsSubscriptionTrafficExhausted(string sourceKey)
    {
        if (_settings.SubscriptionSources.TryGetValue(sourceKey, out var source) && source.TrafficExhausted)
        {
            return true;
        }

        return SubscriptionTrafficHelper.AreProfilesTrafficExhausted(
            GetProfilesForSubscription(IdentityFromSourceKey(sourceKey)));
    }

    private bool ShouldTreatRefreshFailureAsTrafficExhausted(
        SubscriptionGroupIdentity scope,
        string? errorMessage,
        Exception? exception = null)
    {
        if (IsSubscriptionTrafficExhausted(scope.SourceKey))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(errorMessage) &&
            SubscriptionTrafficHelper.IsLikelyTrafficExhaustedErrorMessage(errorMessage))
        {
            return true;
        }

        if (exception is not null && SubscriptionTrafficHelper.IsLikelyTrafficExhaustedError(exception))
        {
            return true;
        }

        return exception is TimeoutException &&
               SubscriptionTrafficHelper.AreProfilesTrafficExhausted(GetProfilesForSubscription(scope));
    }

    private void SetSubscriptionTrafficExhausted(string sourceKey, bool save = true)
    {
        if (!_settings.SubscriptionSources.TryGetValue(sourceKey, out var source))
        {
            source = new SubscriptionSource();
            _settings.SubscriptionSources[sourceKey] = source;
        }

        var subscriptionName = LocalSubscriptionHelper.GetSourceKeySubscriptionName(sourceKey);
        source.TrafficExhausted = true;
        source.DisplayName = RemoveInvalidSubscriptionSuffix(source.DisplayName ?? subscriptionName);

        if (save)
        {
            _settingsStore.Save(_settings);
        }

        if (_isUiReady)
        {
            ProfilesGrid.Items.Refresh();
            RefreshSubscriptionFilterOptions();
        }

        DiagnosticLogService.Warning(
            $"Subscription \"{LocalSubscriptionHelper.GetSourceKeySubscriptionName(sourceKey)}\" traffic exhausted; auto refresh paused.");
    }

    private void ClearSubscriptionTrafficExhausted(string sourceKey)
    {
        if (_settings.SubscriptionSources.TryGetValue(sourceKey, out var source))
        {
            source.TrafficExhausted = false;
        }
    }

    private void ReconcileSubscriptionTrafficExhaustedState()
    {
        foreach (var subscriptionName in _settings.SubscriptionSources.Keys.ToList())
        {
            if (!IsSubscriptionTrafficExhausted(subscriptionName))
            {
                continue;
            }

            if (_settings.SubscriptionSources.TryGetValue(subscriptionName, out var source))
            {
                source.TrafficExhausted = true;
            }
        }
    }

    private void ShowSubscriptionTrafficExhaustedDialog(string subscriptionName)
    {
        ThemedMessageDialog.Show(
            this,
            $"订阅「{subscriptionName}」流量已用尽。",
            kind: ThemedMessageKind.Warning);
    }

    private SubscriptionSource RegisterServerSubscriptionSource(ServerSubscription subscription, bool isInvalid = false)
    {
        _settings.SubscriptionSources.TryGetValue(subscription.Name, out var existing);
        var source = new SubscriptionSource
        {
            Url = subscription.Url,
            ServerSubscriptionId = subscription.Id,
            DisplayName = FormatSubscriptionDisplayName(subscription.Name, isInvalid),
            AutoRefreshMinutes = existing?.AutoRefreshMinutes,
            CreatedAtUtc = existing?.CreatedAtUtc ?? DateTime.UtcNow,
            TrafficExhausted = existing?.TrafficExhausted ?? false
        };
        _settings.SubscriptionSources[subscription.Name] = source;
        return source;
    }

    private void MarkServerSubscriptionInvalid(ServerSubscription subscription)
    {
        var source = RegisterServerSubscriptionSource(subscription, isInvalid: true);
        _settings.SubscriptionSources[subscription.Name] = source;
    }

    private void MarkSubscriptionSourceInvalid(string subscriptionName)
    {
        if (!_settings.SubscriptionSources.TryGetValue(subscriptionName, out var source))
        {
            return;
        }

        var cleanName = RemoveInvalidSubscriptionSuffix(source.DisplayName ?? subscriptionName);
        source.DisplayName = $"{cleanName}{InvalidSubscriptionSuffix}";
    }

    private static string FormatSubscriptionDisplayName(string subscriptionName, bool isInvalid)
    {
        var cleanName = RemoveInvalidSubscriptionSuffix(subscriptionName);
        return isInvalid ? $"{cleanName}{InvalidSubscriptionSuffix}" : cleanName;
    }

    private static string RemoveInvalidSubscriptionSuffix(string value)
    {
        var trimmed = value.Trim();
        return trimmed.EndsWith(InvalidSubscriptionSuffix, StringComparison.Ordinal)
            ? trimmed[..^InvalidSubscriptionSuffix.Length].TrimEnd()
            : trimmed;
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
        _trafficTimer.Start();
        _ = RefreshTrafficAsync();
    }

    private void StopTrafficMonitor()
    {
        _trafficTimer.Stop();
        _lastTrafficSnapshot = null;
        UpdateTrafficStatsDisplay();
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
        _lastDownSpeedText = running ? $"{FormatBytes(downSpeed)}/s" : "—";
        _lastUpSpeedText = running ? $"{FormatBytes(upSpeed)}/s" : "—";
        var todayText = FormatBytes(_settings.TodayUplinkBytes + _settings.TodayDownlinkBytes);
        var totalText = FormatBytes(_settings.TotalDownlinkBytes + _settings.TotalUplinkBytes);
        TrafficDownSpeedText.Text = _lastDownSpeedText;
        TrafficUpSpeedText.Text = _lastUpSpeedText;
        TrafficTodayText.Text = todayText;
        TrafficTotalText.Text = totalText;
        UpdateTrayStatus();
    }

    private void NavGroupToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Primitives.ToggleButton toggle || toggle.Tag is not string panelName)
        {
            return;
        }

        if (FindName(panelName) is not FrameworkElement panel)
        {
            return;
        }

        if (toggle.IsChecked == true)
        {
            ExpandNavPanel(panel);
        }
        else
        {
            CollapseNavPanel(panel);
        }
    }

    private static void ExpandNavPanel(FrameworkElement panel)
    {
        panel.Visibility = Visibility.Visible;
        panel.UpdateLayout();
        var targetHeight = panel.ActualHeight;
        if (targetHeight <= 0)
        {
            panel.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            targetHeight = panel.DesiredSize.Height;
        }

        if (targetHeight <= 0)
        {
            panel.MaxHeight = double.PositiveInfinity;
            return;
        }

        panel.MaxHeight = 0;
        var animation = new DoubleAnimation
        {
            From = 0,
            To = targetHeight,
            Duration = TimeSpan.FromMilliseconds(220),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        animation.Completed += (_, _) => panel.MaxHeight = double.PositiveInfinity;
        panel.BeginAnimation(FrameworkElement.MaxHeightProperty, animation);
    }

    private static void CollapseNavPanel(FrameworkElement panel)
    {
        var currentHeight = panel.ActualHeight;
        if (currentHeight <= 0)
        {
            panel.Visibility = Visibility.Collapsed;
            return;
        }

        panel.MaxHeight = currentHeight;
        var animation = new DoubleAnimation
        {
            From = currentHeight,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(220),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };
        animation.Completed += (_, _) =>
        {
            panel.Visibility = Visibility.Collapsed;
            panel.MaxHeight = double.PositiveInfinity;
        };
        panel.BeginAnimation(FrameworkElement.MaxHeightProperty, animation);
    }

    private async void RowSetActiveNode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: VmessProfile profile })
        {
            await SwitchToProfileAsync(profile);
        }
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
            ScheduleOpenAiCodexPreWarmIfEnabled();
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
        ScheduleOpenAiCodexPreWarmIfEnabled();
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
        var dialog = new CustomRoutingDialog(_settings.CustomRouting, _settings.RoutingMode) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        _settings.CustomRouting = dialog.Routing;
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

        EditRoutingButton.Visibility = Visibility.Visible;
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

    private void ProfilesContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.ContextMenu menu)
        {
            return;
        }

        var visibility = ProfilesGrid.SelectedItems.Count == 1
            ? Visibility.Visible
            : Visibility.Collapsed;

        foreach (var item in menu.Items.OfType<FrameworkElement>()
                     .Where(item => string.Equals(item.Tag?.ToString(), "SingleSelectionOnly", StringComparison.Ordinal)))
        {
            item.Visibility = visibility;
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
        NodeListScroll.ScrollToVerticalOffset(NodeListScroll.VerticalOffset - e.Delta);
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

        Clipboard.SetText(string.Join(Environment.NewLine, selected.Select(ShareLinkBuilder.Build)));
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
            MarkProfileAsLocalManual(saved);
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

    private void OpenNewNodeDialog()
    {
        var dialog = new NodeEditDialog(null) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var saved = dialog.Profile;
        ProfileMetadataHelper.ApplyNew(saved);
        MarkProfileAsLocalManual(saved);
        _profiles.Add(saved);
        SaveProfiles(saved.Id);
        RefreshNodePicker();
        ProfilesGrid.SelectedItem = saved;
        ScheduleRegionEnrichment([saved]);
    }

    private void OpenExportNodeDialog(VmessProfile profile)
    {
        var dialog = new ExportNodeDialog(profile, this);
        dialog.ShowDialog();
    }

    private void InlineNewNodeButton_Click(object sender, RoutedEventArgs e) => OpenNewNodeDialog();

    private void OpenImportDialog()
    {
        var dialog = new ImportDialog { Owner = this };
        if (dialog.ShowDialog() != true || dialog.ImportResult is not SubscriptionImportResult result)
        {
            return;
        }

        _ = AddImportedProfilesAsync(result);
    }

    private void RefreshNodePicker()
    {
        NodePickerCombo.ItemsSource = null;
        NodePickerCombo.ItemsSource = _profiles;
        NodePickerCombo.IsEditable = true;
        SyncNodePickerDisplay();
    }

    private void NodeListNavButton_Click(object sender, RoutedEventArgs e) => ShowNodePage();

    private void CtxNewNode_Click(object sender, RoutedEventArgs e) => OpenNewNodeDialog();

    private void CtxImportNode_Click(object sender, RoutedEventArgs e) => ShowImportPage();

    private void ImportNodeNavButton_Click(object sender, RoutedEventArgs e) => ShowImportPage();

    private void NewNodeNavButton_Click(object sender, RoutedEventArgs e) => OpenNewNodeDialog();

    private void GithubNavButton_Click(object sender, RoutedEventArgs e) => OpenPath(ProjectUrl);

    private void LogNavButton_Click(object sender, RoutedEventArgs e) => ShowLogPage();

    private void AnnouncementNavButton_Click(object sender, RoutedEventArgs e) => ShowAnnouncementPage();

    private void ContactAdminNavButton_Click(object sender, RoutedEventArgs e) => ShowContactAdminPage();

    private void VersionUpdateNavButton_Click(object sender, RoutedEventArgs e) => ShowVersionUpdatePage();

    private void VersionUpdateLoginButton_Click(object sender, RoutedEventArgs e) => ShowLoginPage();

    private void AnnouncementLoginButton_Click(object sender, RoutedEventArgs e) => ShowLoginPage();

    private void ContactAdminLoginButton_Click(object sender, RoutedEventArgs e) => ShowLoginPage();

    private void ShowAnnouncementPage()
    {
        ShowAnnouncementListView();
        UpdateNotificationPagesAuthState();
        ShowPage(AnnouncementPageScroll, AnnouncementNavButton);
        if (_authService.IsAuthenticated)
        {
            _ = LoadAnnouncementsAsync();
        }
    }

    private void ShowContactAdminPage()
    {
        ClearUnreadAdminChatCount();
        UpdateNotificationPagesAuthState();
        ShowPage(ContactAdminPageScroll, ContactAdminNavButton);
        HideChatMessageToast(animate: false);
        if (_authService.IsAuthenticated)
        {
            _ = LoadChatMessagesAsync();
            UpdateChatConnectionStatus(_backendWebSocket.IsConnected ? "connected" : "disconnected");
        }
    }

    private void UpdateNotificationPagesAuthState()
    {
        if (!_isUiReady)
        {
            return;
        }

        var isAuthenticated = _authService.IsAuthenticated;
        if (AnnouncementGuestPanel is not null)
        {
            AnnouncementGuestPanel.Visibility = isAuthenticated ? Visibility.Collapsed : Visibility.Visible;
        }

        if (AnnouncementAuthenticatedPanel is not null)
        {
            AnnouncementAuthenticatedPanel.Visibility = isAuthenticated ? Visibility.Visible : Visibility.Collapsed;
        }

        if (AnnouncementDetailView is not null && !isAuthenticated)
        {
            AnnouncementDetailView.Visibility = Visibility.Collapsed;
        }

        if (!isAuthenticated)
        {
            AnnouncementItemsScroll.Visibility = Visibility.Collapsed;
            AnnouncementEmptyPanel.Visibility = Visibility.Collapsed;
        }

        if (ContactAdminGuestPanel is not null)
        {
            ContactAdminGuestPanel.Visibility = isAuthenticated ? Visibility.Collapsed : Visibility.Visible;
        }

        if (ContactAdminChatPanel is not null)
        {
            ContactAdminChatPanel.Visibility = isAuthenticated ? Visibility.Visible : Visibility.Collapsed;
        }

        if (VersionUpdateGuestPanel is not null)
        {
            VersionUpdateGuestPanel.Visibility = isAuthenticated ? Visibility.Collapsed : Visibility.Visible;
        }

        if (!isAuthenticated)
        {
            VersionUpdateUpToDatePanel.Visibility = Visibility.Collapsed;
            VersionUpdateAvailableScroll.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowVersionUpdatePage()
    {
        UpdateNotificationPagesAuthState();
        ShowPage(VersionUpdatePageScroll, VersionUpdateNavButton);
        if (_authService.IsAuthenticated)
        {
            _ = RefreshLatestVersionAsync();
        }
        else
        {
            SetVersionUpdateStatus("请先登录后查看版本更新。");
        }
    }

    private async Task CheckAppUpdateOnStartupAsync()
    {
        if (!_authService.IsAuthenticated)
        {
            return;
        }

        if (!await _authService.TryRestoreSessionAsync())
        {
            return;
        }

        await RefreshLatestVersionAsync(showLoadingStatus: false, triggerAutoDownload: true);
    }

    private async void RefreshVersionUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshLatestVersionAsync(triggerAutoDownload: false);
    }

    private async Task RefreshLatestVersionAsync(bool showLoadingStatus = true, bool triggerAutoDownload = false)
    {
        if (!_authService.IsAuthenticated)
        {
            SetVersionUpdateStatus("请先登录后查看版本更新。");
            return;
        }

        if (!await _authService.TryRestoreSessionAsync())
        {
            SetVersionUpdateStatus("登录已过期，请重新登录。", isError: true);
            UpdateNotificationPagesAuthState();
            return;
        }

        if (showLoadingStatus)
        {
            SetVersionUpdateStatus("正在检查最新版本...");
        }

        RefreshVersionUpdateButton.IsEnabled = false;
        try
        {
            var result = await _authService.UpdateApi.GetLatestAsync("WINDOWS");
            if (!result.IsSuccess)
            {
                SetVersionUpdateStatus(result.Message, isError: true);
                return;
            }

            _cachedLatestRelease = result.Data;
            RestoreDownloadedUpdateState(_cachedLatestRelease?.Id);
            RenderVersionUpdateUi();

            if (_cachedLatestRelease is not null && IsRequiredVersionUpdate(_cachedLatestRelease))
            {
                await EnsureRequiredVersionUpdateDialogAsync(_cachedLatestRelease);
            }
            else if (triggerAutoDownload && _cachedLatestRelease is not null && IsAppUpdateAvailable(_cachedLatestRelease))
            {
                await TryAutoDownloadVersionUpdateAsync(_cachedLatestRelease, promptWhenReady: true);
            }
        }
        catch (Exception ex)
        {
            SetVersionUpdateStatus($"检查更新失败：{ex.Message}", isError: true);
            DiagnosticLogService.Error("Failed to refresh latest app version.", ex);
        }
        finally
        {
            RefreshVersionUpdateButton.IsEnabled = true;
        }
    }

    private void RenderVersionUpdateUi()
    {
        var currentVersion = AppVersionHelper.GetCurrentVersionName();
        if (_cachedLatestRelease is null || _cachedLatestRelease.File is null)
        {
            VersionUpdateGuestPanel.Visibility = _authService.IsAuthenticated ? Visibility.Collapsed : Visibility.Visible;
            VersionUpdateUpToDatePanel.Visibility = _authService.IsAuthenticated ? Visibility.Visible : Visibility.Collapsed;
            VersionUpdateAvailableScroll.Visibility = Visibility.Collapsed;
            VersionUpdateBadge.Visibility = Visibility.Collapsed;
            VersionUpdateCurrentOnlyText.Text = $"当前版本 v{currentVersion}";
            SetVersionUpdateStatus("当前已是最新版本");
            ResetVersionUpdateDownloadUi();
            return;
        }

        if (!IsAppUpdateAvailable(_cachedLatestRelease))
        {
            VersionUpdateGuestPanel.Visibility = Visibility.Collapsed;
            VersionUpdateUpToDatePanel.Visibility = Visibility.Visible;
            VersionUpdateAvailableScroll.Visibility = Visibility.Collapsed;
            VersionUpdateBadge.Visibility = Visibility.Collapsed;
            VersionUpdateCurrentOnlyText.Text = $"当前版本 v{currentVersion}";
            SetVersionUpdateStatus("当前已是最新版本");
            ResetVersionUpdateDownloadUi();
            return;
        }

        VersionUpdateGuestPanel.Visibility = Visibility.Collapsed;
        VersionUpdateUpToDatePanel.Visibility = Visibility.Collapsed;
        VersionUpdateAvailableScroll.Visibility = Visibility.Visible;
        VersionUpdateBadge.Visibility = Visibility.Visible;
        VersionUpdateBadgeText.Text = $"可更新至 v{_cachedLatestRelease.VersionName}";
        VersionUpdateTitleText.Text = _cachedLatestRelease.Title;
        VersionUpdateVersionLineText.Text = $"v{currentVersion} → v{_cachedLatestRelease.VersionName}（Build {_cachedLatestRelease.VersionCode}）";
        VersionUpdateContentText.Text = string.IsNullOrWhiteSpace(_cachedLatestRelease.Content)
            ? "暂无更新说明。"
            : _cachedLatestRelease.Content;
        VersionUpdatePublishedAtText.Text = $"发布时间：{FormatAnnouncementTime(_cachedLatestRelease.PublishedAt)}";
        VersionUpdateFileNameText.Text = _cachedLatestRelease.File.Filename;
        VersionUpdateFileSizeText.Text = FormatBytes(_cachedLatestRelease.File.FileSize);
        VersionUpdateSha256Text.Text = _cachedLatestRelease.File.Sha256;
        VersionUpdateDownloadLink.NavigateUri = string.IsNullOrWhiteSpace(_cachedLatestRelease.File.DownloadUrl)
            ? null
            : new Uri(_cachedLatestRelease.File.DownloadUrl);
        SetVersionUpdateStatus($"发现新版本 v{_cachedLatestRelease.VersionName}");

        var installReady = _downloadedReleaseId == _cachedLatestRelease.Id &&
                           !string.IsNullOrWhiteSpace(_downloadedUpdatePath) &&
                           File.Exists(_downloadedUpdatePath);
        VersionUpdateInstallButton.IsEnabled = installReady;
        VersionUpdateDownloadButton.IsEnabled = !_isDownloadingAppUpdate;
        VersionUpdateDownloadButton.Content = installReady ? "重新下载" : "下载安装包";
    }

    private static bool IsAppUpdateAvailable(AppUpdateRelease release) =>
        AppVersionHelper.IsUpdateAvailable(release.VersionCode, release.VersionName);

    private static bool IsRequiredVersionUpdate(AppUpdateRelease release) =>
        release.ForceUpdate &&
        release.File is not null &&
        IsAppUpdateAvailable(release);

    private async Task EnsureRequiredVersionUpdateDialogAsync(AppUpdateRelease release)
    {
        if (_requiredVersionUpdateDialog is { IsVisible: true })
        {
            return;
        }

        _requiredVersionUpdateDialog = new RequiredVersionUpdateDialog(release, AppVersionHelper.GetCurrentVersionName())
        {
            Owner = this
        };
        _requiredVersionUpdateDialog.ActionRequested += () => OnRequiredVersionUpdateAction(release);
        _requiredVersionUpdateDialog.Closed += (_, _) =>
        {
            if (!_isExiting)
            {
                IsEnabled = true;
            }

            _requiredVersionUpdateDialog = null;
        };

        IsEnabled = false;
        _requiredVersionUpdateDialog.Show();

        var installReady = _downloadedReleaseId == release.Id &&
                           !string.IsNullOrWhiteSpace(_downloadedUpdatePath) &&
                           File.Exists(_downloadedUpdatePath);
        if (installReady)
        {
            _requiredVersionUpdateDialog.SetInstallReadyWithoutDownload();
            return;
        }

        await DownloadVersionUpdateAsync(release, promptWhenReady: false, requiredDialog: _requiredVersionUpdateDialog);
    }

    private void OnRequiredVersionUpdateAction(AppUpdateRelease release)
    {
        var installReady = _downloadedReleaseId == release.Id &&
                           !string.IsNullOrWhiteSpace(_downloadedUpdatePath) &&
                           File.Exists(_downloadedUpdatePath);
        if (installReady)
        {
            LaunchDownloadedVersionInstaller();
            return;
        }

        if (_requiredVersionUpdateDialog is not null)
        {
            _ = DownloadVersionUpdateAsync(release, promptWhenReady: false, requiredDialog: _requiredVersionUpdateDialog);
        }
    }

    private void SetVersionUpdateStatus(string message, bool isError = false)
    {
        if (VersionUpdateStatusText is null)
        {
            return;
        }

        VersionUpdateStatusText.Text = message;
        VersionUpdateStatusText.Foreground = isError
            ? (System.Windows.Media.Brush)FindResource("RedBrush")
            : (System.Windows.Media.Brush)FindResource("MutedBrush");
    }

    private void ResetVersionUpdateDownloadUi()
    {
        VersionUpdateProgressPanel.Visibility = Visibility.Collapsed;
        VersionUpdateProgressBar.Value = 0;
        VersionUpdateInstallButton.IsEnabled = false;
        if (VersionUpdateDownloadButton is not null)
        {
            VersionUpdateDownloadButton.IsEnabled = !_isDownloadingAppUpdate;
            VersionUpdateDownloadButton.Content = "下载安装包";
        }
    }

    private async Task TryAutoDownloadVersionUpdateAsync(AppUpdateRelease release, bool promptWhenReady)
    {
        if (!_settings.AutoDownloadNewVersion || _isDownloadingAppUpdate)
        {
            return;
        }

        if (_downloadedReleaseId == release.Id &&
            !string.IsNullOrWhiteSpace(_downloadedUpdatePath) &&
            File.Exists(_downloadedUpdatePath))
        {
            SetVersionUpdateStatus($"v{release.VersionName} 已下载，可直接安装。");
            RenderVersionUpdateUi();
            return;
        }

        await DownloadVersionUpdateAsync(release, promptWhenReady);
    }

    private async void VersionUpdateDownloadButton_Click(object sender, RoutedEventArgs e)
    {
        if (_cachedLatestRelease is null)
        {
            await RefreshLatestVersionAsync();
            return;
        }

        await DownloadVersionUpdateAsync(_cachedLatestRelease, promptWhenReady: true);
    }

    private async Task DownloadVersionUpdateAsync(
        AppUpdateRelease release,
        bool promptWhenReady,
        RequiredVersionUpdateDialog? requiredDialog = null)
    {
        if (_isDownloadingAppUpdate)
        {
            return;
        }

        _appUpdateDownloadCts?.Cancel();
        _appUpdateDownloadCts?.Dispose();
        _appUpdateDownloadCts = new CancellationTokenSource();
        var token = _appUpdateDownloadCts.Token;

        _isDownloadingAppUpdate = true;
        VersionUpdateDownloadButton.IsEnabled = false;
        VersionUpdateInstallButton.IsEnabled = false;
        VersionUpdateProgressPanel.Visibility = Visibility.Visible;
        VersionUpdateProgressBar.IsIndeterminate = false;
        VersionUpdateProgressBar.Value = 0;
        SetVersionUpdateStatus($"正在下载 v{release.VersionName}...");
        requiredDialog?.SetDownloading();

        try
        {
            var path = await _appUpdateDownloadService.DownloadAsync(
                release,
                new Progress<Services.DownloadProgress>(progress => UpdateVersionDownloadProgress(progress, requiredDialog)),
                token);
            _downloadedUpdatePath = path;
            _downloadedReleaseId = release.Id;
            PersistDownloadedUpdateState(release.Id, path);
            SetVersionUpdateStatus($"v{release.VersionName} 下载完成，SHA-256 校验通过。");
            VersionUpdateInstallButton.IsEnabled = true;
            VersionUpdateDownloadButton.Content = "重新下载";
            RenderVersionUpdateUi();
            requiredDialog?.SetReadyToInstall();

            if (promptWhenReady && requiredDialog is null)
            {
                MessageBox.Show(
                    $"新版本 v{release.VersionName} 已下载完成。\n\n请前往「通知 → 版本更新」点击「立即安装」，或稍后在此页面安装。",
                    "版本更新",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }
        catch (OperationCanceledException)
        {
            SetVersionUpdateStatus("下载已取消。");
            requiredDialog?.SetDownloadFailed("下载已取消，请重试。");
        }
        catch (Exception ex)
        {
            SetVersionUpdateStatus($"下载失败：{ex.Message}", isError: true);
            requiredDialog?.SetDownloadFailed($"下载失败：{ex.Message}");
            DiagnosticLogService.Error("App update download failed.", ex);
        }
        finally
        {
            _isDownloadingAppUpdate = false;
            VersionUpdateDownloadButton.IsEnabled = true;
            if (VersionUpdateProgressPanel.Visibility == Visibility.Visible && VersionUpdateProgressBar.Value >= 100)
            {
                VersionUpdateProgressPanel.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void UpdateVersionDownloadProgress(Services.DownloadProgress progress, RequiredVersionUpdateDialog? requiredDialog = null)
    {
        if (progress.TotalBytes > 0)
        {
            var percentage = Math.Clamp(progress.DownloadedBytes * 100d / progress.TotalBytes, 0, 100);
            VersionUpdateProgressBar.IsIndeterminate = false;
            VersionUpdateProgressBar.Value = percentage;
            VersionUpdateProgressText.Text = $"正在下载：{FormatBytes(progress.DownloadedBytes)} / {FormatBytes(progress.TotalBytes)}";
            requiredDialog?.UpdateProgress(
                percentage,
                $"正在下载：{FormatBytes(progress.DownloadedBytes)} / {FormatBytes(progress.TotalBytes)}");
            return;
        }

        VersionUpdateProgressBar.IsIndeterminate = true;
        VersionUpdateProgressText.Text = $"正在下载：{FormatBytes(progress.DownloadedBytes)} / 未知大小";
        requiredDialog?.UpdateProgress(0, $"正在下载：{FormatBytes(progress.DownloadedBytes)} / 未知大小");
    }

    private void VersionUpdateInstallButton_Click(object sender, RoutedEventArgs e) => LaunchDownloadedVersionInstaller();

    private void LaunchDownloadedVersionInstaller()
    {
        if (string.IsNullOrWhiteSpace(_downloadedUpdatePath) || !File.Exists(_downloadedUpdatePath))
        {
            SetVersionUpdateStatus("安装包不存在，请先下载。", isError: true);
            return;
        }

        LaunchInstallerAndExit(_downloadedUpdatePath);
    }

    private void LaunchInstallerAndExit(string installerPath)
    {
        try
        {
            foreach (Window window in Application.Current.Windows)
            {
                window.Hide();
            }
        }
        catch
        {
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = installerPath,
                Arguments = "/CLOSEAPPLICATIONS",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            SetVersionUpdateStatus($"无法启动安装程序：{ex.Message}", isError: true);
            DiagnosticLogService.Warning($"Failed to launch installer: {ex.Message}");
            return;
        }

        _isExiting = true;
        Dispatcher.BeginInvoke(async () =>
        {
            await Task.Delay(1000);
            Application.Current.Shutdown();
        }, DispatcherPriority.Background);
    }

    private void VersionUpdateOpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Nexora",
            "updates");
        Directory.CreateDirectory(directory);
        OpenPath(directory);
    }

    private void VersionUpdateDownloadLink_Click(object sender, RoutedEventArgs e)
    {
        if (_cachedLatestRelease?.File?.DownloadUrl is not { Length: > 0 } url)
        {
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true
        });
    }

    private void RestoreDownloadedUpdateState(int? expectedReleaseId = null)
    {
        if (_settings.DownloadedUpdateReleaseId is not int releaseId ||
            string.IsNullOrWhiteSpace(_settings.DownloadedUpdateFilePath) ||
            !File.Exists(_settings.DownloadedUpdateFilePath))
        {
            ClearPersistedDownloadedUpdateState();
            return;
        }

        if (expectedReleaseId is int expected && releaseId != expected)
        {
            _downloadedReleaseId = null;
            _downloadedUpdatePath = null;
            return;
        }

        _downloadedReleaseId = releaseId;
        _downloadedUpdatePath = _settings.DownloadedUpdateFilePath;
    }

    private void PersistDownloadedUpdateState(int releaseId, string path)
    {
        _settings.DownloadedUpdateReleaseId = releaseId;
        _settings.DownloadedUpdateFilePath = path;
        _settingsStore.Save(_settings);
    }

    private void ClearPersistedDownloadedUpdateState()
    {
        _downloadedReleaseId = null;
        _downloadedUpdatePath = null;
        if (_settings.DownloadedUpdateReleaseId is null && string.IsNullOrWhiteSpace(_settings.DownloadedUpdateFilePath))
        {
            return;
        }

        _settings.DownloadedUpdateReleaseId = null;
        _settings.DownloadedUpdateFilePath = null;
        _settingsStore.Save(_settings);
    }

    private void SyncAutoDownloadUpdateFromSettings()
    {
        if (!_isUiReady || AutoDownloadNewVersionToggle is null)
        {
            return;
        }

        _suppressAutoDownloadNewVersionToggleEvent = true;
        AutoDownloadNewVersionToggle.IsChecked = _settings.AutoDownloadNewVersion;
        _suppressAutoDownloadNewVersionToggleEvent = false;
    }

    private void AutoDownloadNewVersionToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!_isUiReady || _suppressAutoDownloadNewVersionToggleEvent)
        {
            return;
        }

        _settings.AutoDownloadNewVersion = AutoDownloadNewVersionToggle.IsChecked == true;
        _settingsStore.Save(_settings);
    }

    private async void RefreshAnnouncementsButton_Click(object sender, RoutedEventArgs e)
    {
        await LoadAnnouncementsAsync();
    }

    private async Task LoadAnnouncementsAsync(string? preserveDetailTitle = null)
    {
        if (!_authService.IsAuthenticated)
        {
            SetAnnouncementStatus("请先登录后查看公告。");
            return;
        }

        if (!await _authService.TryRestoreSessionAsync())
        {
            SetAnnouncementStatus("登录已过期，请重新登录。", isError: true);
            UpdateNotificationPagesAuthState();
            return;
        }

        SetAnnouncementStatus("正在加载公告...");
        RefreshAnnouncementsButton.IsEnabled = false;
        try
        {
            var result = await _authService.NotificationApi.ListAsync();
            if (!result.IsSuccess || result.Data is null)
            {
                SetAnnouncementStatus(result.Message, isError: true);
                return;
            }

            _announcements.Clear();
            foreach (var item in result.Data.OrderByDescending(n => n.CreatedAt))
            {
                _announcements.Add(item);
            }

            RenderAnnouncementList();
            var unreadCount = _announcements.Count(item => !item.Read);
            UpdateAnnouncementUnreadBadge(unreadCount);
            SetAnnouncementStatus(_announcements.Count == 0
                ? "暂无公告"
                : $"共 {_announcements.Count} 条公告");

            if (!string.IsNullOrWhiteSpace(preserveDetailTitle))
            {
                var current = _announcements.FirstOrDefault(item =>
                    string.Equals(item.Title, preserveDetailTitle, StringComparison.Ordinal));
                if (current is not null)
                {
                    ShowAnnouncementDetailView(current);
                }
                else
                {
                    ShowAnnouncementListView();
                }
            }
        }
        catch (Exception ex)
        {
            SetAnnouncementStatus($"加载公告失败：{ex.Message}", isError: true);
            DiagnosticLogService.Error("Failed to load announcements.", ex);
        }
        finally
        {
            RefreshAnnouncementsButton.IsEnabled = true;
        }
    }

    private void SetAnnouncementStatus(string message, bool isError = false)
    {
        if (AnnouncementStatusText is null)
        {
            return;
        }

        AnnouncementStatusText.Text = message;
        AnnouncementStatusText.Foreground = isError
            ? (System.Windows.Media.Brush)FindResource("RedBrush")
            : (System.Windows.Media.Brush)FindResource("MutedBrush");
    }

    private void ShowAnnouncementListView()
    {
        if (AnnouncementListView is not null)
        {
            AnnouncementListView.Visibility = Visibility.Visible;
        }

        if (AnnouncementDetailView is not null)
        {
            AnnouncementDetailView.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowAnnouncementDetailView(NotificationItem notification)
    {
        if (AnnouncementListView is not null)
        {
            AnnouncementListView.Visibility = Visibility.Collapsed;
        }

        if (AnnouncementDetailView is not null)
        {
            AnnouncementDetailView.Visibility = Visibility.Visible;
        }

        AnnouncementDetailTitleText.Text = notification.Title;
        AnnouncementDetailBodyText.Text = notification.DisplayBody;
        AnnouncementDetailTimeText.Text = FormatAnnouncementTime(notification.CreatedAt);
        ApplyAnnouncementLevelStyle(AnnouncementDetailLevelBadge, AnnouncementDetailLevelText, notification.DisplayLevel);
    }

    private void RenderAnnouncementList()
    {
        AnnouncementItemsPanel.Children.Clear();
        if (_announcements.Count == 0)
        {
            AnnouncementItemsScroll.Visibility = Visibility.Collapsed;
            AnnouncementEmptyPanel.Visibility = Visibility.Visible;
            return;
        }

        AnnouncementEmptyPanel.Visibility = Visibility.Collapsed;
        AnnouncementItemsScroll.Visibility = Visibility.Visible;
        var announcementIcon = TryLoadAppBitmap("assets/icons/announcement.png");
        foreach (var notification in _announcements)
        {
            AnnouncementItemsPanel.Children.Add(CreateAnnouncementCard(notification, announcementIcon));
        }
    }

    private UIElement CreateAnnouncementCard(NotificationItem notification, BitmapImage? announcementIcon)
    {
        var cardButton = new System.Windows.Controls.Button
        {
            Style = (Style)FindResource("AnnouncementCardButtonStyle"),
            Tag = notification
        };
        cardButton.Click += AnnouncementCardButton_Click;

        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var iconHost = new Border
        {
            Width = 42,
            Height = 42,
            CornerRadius = new CornerRadius(12),
            Background = notification.Read
                ? new SolidColorBrush(Color.FromRgb(248, 250, 252))
                : new SolidColorBrush(Color.FromRgb(239, 246, 255)),
            BorderBrush = new SolidColorBrush(notification.Read ? Color.FromRgb(226, 232, 240) : Color.FromRgb(191, 219, 254)),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 14, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        if (announcementIcon is not null)
        {
            iconHost.Child = new System.Windows.Controls.Image
            {
                Source = announcementIcon,
                Width = 22,
                Height = 22,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = notification.Read ? 0.72 : 1
            };
        }

        Grid.SetColumn(iconHost, 0);
        root.Children.Add(iconHost);

        var contentPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var titleRow = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titleText = new TextBlock
        {
            Text = notification.Title,
            FontSize = 16,
            FontWeight = notification.Read ? FontWeights.SemiBold : FontWeights.Bold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 10, 0)
        };
        Grid.SetColumn(titleText, 0);
        titleRow.Children.Add(titleText);

        var levelBadge = new Border
        {
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(8, 2, 8, 2),
            VerticalAlignment = VerticalAlignment.Center
        };
        var levelText = new TextBlock
        {
            FontSize = 12,
            FontWeight = FontWeights.SemiBold
        };
        ApplyAnnouncementLevelStyle(levelBadge, levelText, notification.DisplayLevel);
        levelBadge.Child = levelText;
        Grid.SetColumn(levelBadge, 1);
        titleRow.Children.Add(levelBadge);
        contentPanel.Children.Add(titleRow);

        contentPanel.Children.Add(new TextBlock
        {
            Text = TruncateAnnouncementPreview(notification.DisplayBody),
            Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139)),
            FontSize = 14,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxHeight = 22
        });
        contentPanel.Children.Add(new TextBlock
        {
            Text = FormatAnnouncementTime(notification.CreatedAt),
            Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
            FontSize = 12,
            Margin = new Thickness(0, 8, 0, 0)
        });
        Grid.SetColumn(contentPanel, 1);
        root.Children.Add(contentPanel);

        var trailingPanel = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(14, 0, 0, 0)
        };
        trailingPanel.Orientation = System.Windows.Controls.Orientation.Horizontal;
        if (!notification.Read)
        {
            trailingPanel.Children.Add(new Border
            {
                Width = 8,
                Height = 8,
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(Color.FromRgb(37, 99, 235)),
                Margin = new Thickness(0, 0, 10, 0)
            });
        }

        trailingPanel.Children.Add(new TextBlock
        {
            Text = "›",
            FontSize = 22,
            Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetColumn(trailingPanel, 2);
        root.Children.Add(trailingPanel);

        cardButton.Content = root;
        return cardButton;
    }

    private async void AnnouncementCardButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: NotificationItem notification })
        {
            return;
        }

        ShowAnnouncementDetailView(notification);
        await MarkAnnouncementReadAsync(notification);
    }

    private void AnnouncementBackButton_Click(object sender, RoutedEventArgs e)
    {
        ShowAnnouncementListView();
    }

    private async Task MarkAnnouncementReadAsync(NotificationItem notification)
    {
        if (notification.Read)
        {
            return;
        }

        try
        {
            var result = await _authService.NotificationApi.MarkReadAsync(notification.Id);
            if (result.IsSuccess)
            {
                notification.Read = true;
                RenderAnnouncementList();
                UpdateAnnouncementUnreadBadge(_announcements.Count(item => !item.Read));
            }
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Warning($"Failed to mark notification read: {ex.Message}");
        }
    }

    private void UpdateAnnouncementUnreadBadge(int unreadCount)
    {
        if (AnnouncementUnreadBadge is null || AnnouncementUnreadBadgeText is null)
        {
            return;
        }

        if (unreadCount <= 0)
        {
            AnnouncementUnreadBadge.Visibility = Visibility.Collapsed;
            return;
        }

        AnnouncementUnreadBadge.Visibility = Visibility.Visible;
        AnnouncementUnreadBadgeText.Text = $"{unreadCount} 条未读";
    }

    private void IncrementUnreadAdminChatCount()
    {
        _unreadAdminChatCount++;
        UpdateContactAdminUnreadBadge(_unreadAdminChatCount);
        StartTaskbarAttention();
    }

    private void ClearUnreadAdminChatCount()
    {
        if (_unreadAdminChatCount <= 0)
        {
            return;
        }

        _unreadAdminChatCount = 0;
        UpdateContactAdminUnreadBadge(0);
        StopTaskbarAttention();
    }

    private void UpdateContactAdminUnreadBadge(int unreadCount)
    {
        if (ContactAdminUnreadBadge is null || ContactAdminUnreadBadgeText is null)
        {
            return;
        }

        if (unreadCount <= 0)
        {
            ContactAdminUnreadBadge.Visibility = Visibility.Collapsed;
            return;
        }

        ContactAdminUnreadBadge.Visibility = Visibility.Visible;
        ContactAdminUnreadBadgeText.Text = unreadCount > 99 ? "99+" : unreadCount.ToString();
    }

    private void StartTaskbarAttention()
    {
        if (_unreadAdminChatCount <= 0)
        {
            return;
        }

        if (!IsActive)
        {
            FlashTaskbar();
        }

        if (_trayBlinkTimer.IsEnabled)
        {
            return;
        }

        _trayIconBlinkVisible = true;
        if (_trayIcon is not null && _trayIconNormal is not null)
        {
            _trayIcon.Icon = _trayIconNormal;
        }

        _trayBlinkTimer.Start();
    }

    private void StopTaskbarAttention()
    {
        _trayBlinkTimer.Stop();
        if (_trayIcon is not null && _trayIconNormal is not null)
        {
            _trayIcon.Icon = _trayIconNormal;
        }
    }

    private void TrayBlinkTimer_Tick(object? sender, EventArgs e)
    {
        if (_trayIcon is null || _trayIconNormal is null || _trayIconBlank is null)
        {
            return;
        }

        if (_unreadAdminChatCount <= 0)
        {
            StopTaskbarAttention();
            return;
        }

        _trayIconBlinkVisible = !_trayIconBlinkVisible;
        _trayIcon.Icon = _trayIconBlinkVisible ? _trayIconNormal : _trayIconBlank;
    }

    private void FlashTaskbar()
    {
        if (!IsLoaded || IsActive)
        {
            return;
        }

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var info = new FlashWindowInfo
        {
            CbSize = (uint)Marshal.SizeOf<FlashWindowInfo>(),
            Hwnd = hwnd,
            DwFlags = FlashwTray | FlashwTimerNoFg,
            UCount = 8,
            DwTimeout = 0
        };
        FlashWindowEx(ref info);
    }

    private static Drawing.Icon? CreateBlankTrayIcon(Drawing.Icon source)
    {
        try
        {
            using var bitmap = new Drawing.Bitmap(source.Width, source.Height);
            using var graphics = Drawing.Graphics.FromImage(bitmap);
            graphics.Clear(Drawing.Color.Transparent);
            var handle = bitmap.GetHicon();
            return Drawing.Icon.FromHandle(handle);
        }
        catch
        {
            return null;
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FlashWindowInfo pwfi);

    private const uint FlashwTray = 0x00000002;
    private const uint FlashwTimerNoFg = 0x0000000C;

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashWindowInfo
    {
        public uint CbSize;
        public IntPtr Hwnd;
        public uint DwFlags;
        public uint UCount;
        public uint DwTimeout;
    }

    private static string TruncateAnnouncementPreview(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "暂无内容";
        }

        var normalized = text.ReplaceLineEndings(" ").Trim();
        return normalized.Length <= 72 ? normalized : normalized[..72] + "...";
    }

    private static string FormatAnnouncementTime(string createdAt)
    {
        if (DateTime.TryParse(createdAt, out var parsed))
        {
            return parsed.ToString("yyyy-MM-dd HH:mm");
        }

        return createdAt;
    }

    private static void ApplyAnnouncementLevelStyle(Border badge, TextBlock label, string level)
    {
        var normalized = string.IsNullOrWhiteSpace(level) ? "info" : level.Trim().ToLowerInvariant();
        switch (normalized)
        {
            case "warn":
            case "warning":
                badge.Background = new SolidColorBrush(Color.FromRgb(255, 251, 235));
                badge.BorderBrush = new SolidColorBrush(Color.FromRgb(253, 230, 138));
                badge.BorderThickness = new Thickness(1);
                label.Foreground = new SolidColorBrush(Color.FromRgb(180, 83, 9));
                label.Text = "警告";
                break;
            case "error":
            case "danger":
                badge.Background = new SolidColorBrush(Color.FromRgb(254, 242, 242));
                badge.BorderBrush = new SolidColorBrush(Color.FromRgb(252, 165, 165));
                badge.BorderThickness = new Thickness(1);
                label.Foreground = new SolidColorBrush(Color.FromRgb(220, 38, 38));
                label.Text = "重要";
                break;
            case "info":
                badge.Background = new SolidColorBrush(Color.FromRgb(239, 246, 255));
                badge.BorderBrush = new SolidColorBrush(Color.FromRgb(191, 219, 254));
                badge.BorderThickness = new Thickness(1);
                label.Foreground = new SolidColorBrush(Color.FromRgb(29, 78, 216));
                label.Text = "通知";
                break;
            default:
                badge.Background = new SolidColorBrush(Color.FromRgb(248, 250, 252));
                badge.BorderBrush = new SolidColorBrush(Color.FromRgb(226, 232, 240));
                badge.BorderThickness = new Thickness(1);
                label.Foreground = new SolidColorBrush(Color.FromRgb(71, 85, 105));
                label.Text = level;
                break;
        }
    }

    private static BitmapImage? TryLoadBitmapFromFile(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return null;
        }

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(Path.GetFullPath(filePath), UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private async Task LoadChatMessagesAsync()
    {
        if (!_authService.IsAuthenticated)
        {
            SetContactAdminStatus("请先登录后查看管理员消息。");
            return;
        }

        if (!await _authService.TryRestoreSessionAsync())
        {
            SetContactAdminStatus("登录已过期，请重新登录。", isError: true);
            UpdateNotificationPagesAuthState();
            return;
        }

        var userId = _authService.CurrentSession?.UserId ?? 0;
        if (userId <= 0)
        {
            return;
        }

        var cachedMessages = _chatMessageStore.Load(userId);
        var hadCache = cachedMessages.Count > 0;
        if (hadCache)
        {
            RestoreChatMessages(cachedMessages);
            ScrollChatToEnd(force: true);
            SetContactAdminStatus("");
        }
        else
        {
            SetContactAdminStatus("正在加载消息...");
        }

        await SyncChatMessagesFromCloudAsync(userId, hadCache);
    }

    private void RestoreChatMessages(IReadOnlyList<ChatMessage> messages)
    {
        _chatMessages.Clear();
        _chatMessageIds.Clear();
        _chatFileLocalPaths.Clear();
        ChatMessagesPanel.Children.Clear();
        foreach (var message in messages.OrderBy(m => m.CreatedAt))
        {
            AppendChatMessage(message, scroll: false, persist: false);
        }

        UpdateChatEmptyState();
    }

    private async Task SyncChatMessagesFromCloudAsync(int userId, bool hadCache)
    {
        try
        {
            var result = await _authService.ChatApi.ListMessagesAsync();
            if (!result.IsSuccess || result.Data is null)
            {
                if (!hadCache)
                {
                    SetContactAdminStatus(result.Message, isError: true);
                }

                return;
            }

            var cloudMessages = result.Data.OrderBy(m => m.CreatedAt).ToList();
            var hasNewMessages = false;
            foreach (var message in cloudMessages)
            {
                if (_chatMessageIds.Contains(message.Id))
                {
                    continue;
                }

                AppendChatMessage(message, scroll: false, persist: false);
                hasNewMessages = true;
            }

            _chatMessageStore.Save(userId, cloudMessages);
            UpdateChatEmptyState();
            ScrollChatToEnd(force: true);

            if (!hadCache)
            {
                SetContactAdminStatus(cloudMessages.Count == 0 ? "暂无消息，可向管理员发送咨询。" : "");
            }
            else if (hasNewMessages)
            {
                SetContactAdminStatus("");
            }
        }
        catch (Exception ex)
        {
            if (!hadCache)
            {
                SetContactAdminStatus($"加载消息失败：{ex.Message}", isError: true);
            }

            DiagnosticLogService.Error("Failed to sync chat messages.", ex);
        }
    }

    private void SaveChatMessagesToLocalStore()
    {
        var userId = _authService.CurrentSession?.UserId ?? 0;
        if (userId <= 0)
        {
            return;
        }

        _chatMessageStore.Save(userId, _chatMessages);
    }

    private void AppendChatMessage(ChatMessage message, bool scroll = true, bool persist = true)
    {
        if (!_chatMessageIds.Add(message.Id))
        {
            return;
        }

        _chatMessages.Add(message);
        ChatMessagesPanel.Children.Add(CreateChatMessageElement(message));
        if (ChatMessageHelper.HasAttachment(message) && !ChatMessageHelper.IsImageAttachment(message))
        {
            _ = EnsureChatFileDownloadedAsync(message);
        }

        UpdateChatEmptyState();
        if (persist)
        {
            SaveChatMessagesToLocalStore();
        }

        if (scroll)
        {
            ScrollChatToEnd();
        }
    }

    private void UpdateChatEmptyState()
    {
        if (ChatEmptyPanel is null)
        {
            return;
        }

        ChatEmptyPanel.Visibility = _chatMessages.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private UIElement CreateChatMessageElement(ChatMessage message)
    {
        var isAdmin = message.IsFromAdmin;
        var row = new Grid
        {
            Margin = new Thickness(0, 0, 0, 18),
            HorizontalAlignment = isAdmin ? System.Windows.HorizontalAlignment.Left : System.Windows.HorizontalAlignment.Right,
            MaxWidth = 760
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var avatar = CreateChatAvatar(isAdmin);
        var bubbleColumn = 1;
        var avatarColumn = isAdmin ? 0 : 2;

        Grid.SetColumn(avatar, avatarColumn);
        row.Children.Add(avatar);

        var bubblePanel = new StackPanel
        {
            MaxWidth = 540,
            HorizontalAlignment = isAdmin ? System.Windows.HorizontalAlignment.Left : System.Windows.HorizontalAlignment.Right,
            Margin = isAdmin ? new Thickness(12, 0, 0, 0) : new Thickness(0, 0, 12, 0)
        };

        bubblePanel.Children.Add(new TextBlock
        {
            Text = isAdmin ? "管理员" : GetUserDisplayName(),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = isAdmin ? FindThemeBrush("AccentTextBrush") : FindThemeBrush("MutedBrush"),
            Margin = new Thickness(isAdmin ? 4 : 0, 0, isAdmin ? 0 : 4, 6),
            HorizontalAlignment = isAdmin ? System.Windows.HorizontalAlignment.Left : System.Windows.HorizontalAlignment.Right
        });

        var isAttachmentOnly = string.IsNullOrWhiteSpace(message.Content) && !string.IsNullOrWhiteSpace(message.FileUrl);
        var isImageOnly = isAttachmentOnly && ChatMessageHelper.IsImageAttachment(message);
        var isFileOnly = isAttachmentOnly && ChatMessageHelper.HasAttachment(message) && !ChatMessageHelper.IsImageAttachment(message);

        var bubble = new Border
        {
            Background = isAttachmentOnly
                ? System.Windows.Media.Brushes.Transparent
                : isAdmin
                    ? FindThemeBrush("ChatBubbleAdminBrush")
                    : FindThemeBrush("ChatBubbleUserBrush"),
            BorderBrush = isAttachmentOnly
                ? System.Windows.Media.Brushes.Transparent
                : isAdmin
                    ? FindThemeBrush("AccentSoftBorderBrush")
                    : FindThemeBrush("AccentPrimaryBorderBrush"),
            BorderThickness = isAttachmentOnly ? new Thickness(0) : new Thickness(1),
            CornerRadius = isAttachmentOnly
                ? new CornerRadius(0)
                : isAdmin
                    ? new CornerRadius(6, 18, 18, 18)
                    : new CornerRadius(18, 6, 18, 18),
            Padding = isAttachmentOnly ? new Thickness(0) : new Thickness(12),
            HorizontalAlignment = isAdmin ? System.Windows.HorizontalAlignment.Left : System.Windows.HorizontalAlignment.Right
        };

        if (!isAttachmentOnly)
        {
            bubble.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Color.FromRgb(15, 23, 42),
                BlurRadius = 12,
                ShadowDepth = 2,
                Opacity = 0.08
            };
        }

        var bubbleContent = new StackPanel();
        if (!string.IsNullOrWhiteSpace(message.Content))
        {
            bubbleContent.Children.Add(new TextBlock
            {
                Text = message.Content,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 15,
                LineHeight = 22,
                Foreground = isAdmin
                    ? FindThemeBrush("TextBrush")
                    : System.Windows.Media.Brushes.White
            });
        }

        if (ChatMessageHelper.IsImageAttachment(message) && !string.IsNullOrWhiteSpace(message.FileUrl))
        {
            if (bubbleContent.Children.Count > 0)
            {
                bubbleContent.Children.Add(new Border { Height = 8 });
            }

            bubbleContent.Children.Add(CreateChatImageAttachment(message));
        }
        else if (ChatMessageHelper.HasAttachment(message) && !ChatMessageHelper.IsImageAttachment(message))
        {
            if (bubbleContent.Children.Count > 0)
            {
                bubbleContent.Children.Add(new Border { Height = 8 });
            }

            bubbleContent.Children.Add(CreateChatFileAttachmentCard(message, standalone: isFileOnly));
        }

        if (bubbleContent.Children.Count == 0)
        {
            bubbleContent.Children.Add(new TextBlock
            {
                Text = "（空消息）",
                FontSize = 14,
                Foreground = isAdmin
                    ? FindThemeBrush("MutedBrush")
                    : new SolidColorBrush(Color.FromRgb(219, 234, 254))
            });
        }

        bubble.Child = bubbleContent;
        if (!isAttachmentOnly)
        {
            AttachChatMessageContextMenu(bubble, message, copyImage: false);
        }

        bubblePanel.Children.Add(bubble);
        bubblePanel.Children.Add(new TextBlock
        {
            Text = FormatAnnouncementTime(message.CreatedAt),
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
            Margin = new Thickness(isAdmin ? 4 : 0, 8, isAdmin ? 0 : 4, 0),
            HorizontalAlignment = isAdmin ? System.Windows.HorizontalAlignment.Left : System.Windows.HorizontalAlignment.Right
        });

        Grid.SetColumn(bubblePanel, bubbleColumn);
        row.Children.Add(bubblePanel);
        row.Tag = message;
        return row;
    }

    private UIElement CreateChatImageAttachment(ChatMessage message)
    {
        var fileUrl = message.FileUrl ?? "";
        var imageHost = new Border
        {
            CornerRadius = new CornerRadius(12),
            ClipToBounds = true,
            Background = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
            MaxWidth = 320,
            MinHeight = 120,
            MinWidth = 160
        };

        imageHost.Child = new TextBlock
        {
            Text = "图片加载中...",
            Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139)),
            FontSize = 13,
            Margin = new Thickness(12),
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        _ = LoadChatImageAttachmentAsync(imageHost, message, fileUrl);

        imageHost.MouseLeftButtonUp += (_, _) =>
        {
            if (!ShowChatImagePreview(message) && !string.IsNullOrWhiteSpace(fileUrl))
            {
                OpenChatAttachmentUrl(fileUrl);
            }
        };
        AttachChatMessageContextMenu(imageHost, message, copyImage: true, allowSaveAs: true);
        return imageHost;
    }

    private async Task LoadChatImageAttachmentAsync(Border imageHost, ChatMessage message, string fileUrl)
    {
        BitmapImage? bitmap = null;
        if (!string.IsNullOrWhiteSpace(fileUrl))
        {
            if (AvatarImageLoader.TryLoadLocal(fileUrl, out var localBitmap))
            {
                bitmap = localBitmap;
            }
            else
            {
                bitmap = await AvatarImageLoader.LoadBitmapAsync(fileUrl);
            }
        }

        if (!imageHost.CheckAccess())
        {
            await imageHost.Dispatcher.InvokeAsync(() => ApplyChatImageAttachment(imageHost, bitmap));
            return;
        }

        ApplyChatImageAttachment(imageHost, bitmap);
    }

    private static void ApplyChatImageAttachment(Border imageHost, BitmapImage? bitmap)
    {
        if (bitmap is not null)
        {
            imageHost.Child = new System.Windows.Controls.Image
            {
                Source = bitmap,
                MaxHeight = 240,
                Stretch = Stretch.Uniform
            };
            return;
        }

        imageHost.Child = new TextBlock
        {
            Text = "图片加载失败",
            Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139)),
            FontSize = 13,
            Margin = new Thickness(4),
            TextWrapping = TextWrapping.Wrap
        };
    }

    private UIElement CreateChatFileAttachmentCard(ChatMessage message, bool standalone)
    {
        var fileName = string.IsNullOrWhiteSpace(message.FileName) ? "附件文件" : message.FileName.Trim();
        var extensionLabel = ChatMessageHelper.GetFileExtensionLabel(fileName);
        var typeColor = ChatMessageHelper.GetFileTypeColor(fileName);

        var card = new Border
        {
            Background = FindThemeBrush("ChatPanelGlassBrush"),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x99, 0xE2, 0xE8, 0xF0)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            MinWidth = 248,
            MaxWidth = 300,
            Cursor = System.Windows.Input.Cursors.Hand
        };

        if (standalone)
        {
            card.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Color.FromRgb(15, 23, 42),
                BlurRadius = 10,
                ShadowDepth = 1,
                Opacity = 0.06
            };
        }

        var layout = new StackPanel();

        var body = new Grid { Margin = new Thickness(12, 10, 12, 8) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        info.Children.Add(new TextBlock
        {
            Text = fileName,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(17, 24, 39)),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            MaxWidth = 180
        });
        info.Children.Add(new TextBlock
        {
            Text = ChatMessageHelper.FormatFileSizeCompact(message.FileSize),
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(107, 114, 128)),
            Margin = new Thickness(0, 6, 0, 0)
        });
        Grid.SetColumn(info, 0);
        body.Children.Add(info);

        var iconHost = new Border
        {
            Width = 44,
            Height = 52,
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.FromRgb(typeColor.R, typeColor.G, typeColor.B)),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = extensionLabel,
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = System.Windows.Media.Brushes.White,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        Grid.SetColumn(iconHost, 1);
        body.Children.Add(iconHost);
        layout.Children.Add(body);

        var footer = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(243, 244, 246)),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(12, 6, 12, 8)
        };
        var footerRow = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        footerRow.Children.Add(new Border
        {
            Width = 14,
            Height = 14,
            CornerRadius = new CornerRadius(3),
            Background = FindThemeBrush("AccentStrongBrush"),
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        footerRow.Children.Add(new TextBlock
        {
            Text = "Nexora",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(156, 163, 175)),
            VerticalAlignment = VerticalAlignment.Center
        });
        footer.Child = footerRow;
        layout.Children.Add(footer);

        card.Child = layout;
        card.MouseLeftButtonUp += (_, _) => OpenChatFileLocally(message);
        AttachChatMessageContextMenu(card, message, copyImage: false, allowSaveAs: true);
        return card;
    }

    private System.Windows.Media.Brush FindThemeBrush(string key) => (System.Windows.Media.Brush)FindResource(key);

    private async Task EnsureChatFileDownloadedAsync(ChatMessage message)
    {
        if (string.IsNullOrWhiteSpace(message.FileUrl) || ChatMessageHelper.IsImageAttachment(message))
        {
            return;
        }

        try
        {
            var localPath = await _chatAttachmentDownloadService.EnsureDownloadedAsync(message);
            if (!string.IsNullOrWhiteSpace(localPath) && File.Exists(localPath))
            {
                _chatFileLocalPaths[message.Id] = localPath;
            }
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Warning($"Failed to download chat attachment {message.Id}: {ex.Message}");
        }
    }

    private void OpenChatFileLocally(ChatMessage message)
    {
        if (_chatFileLocalPaths.TryGetValue(message.Id, out var cachedPath) && File.Exists(cachedPath))
        {
            LaunchLocalFile(cachedPath);
            return;
        }

        var expectedPath = _chatAttachmentDownloadService.GetLocalPath(message);
        if (File.Exists(expectedPath))
        {
            _chatFileLocalPaths[message.Id] = expectedPath;
            LaunchLocalFile(expectedPath);
            return;
        }

        _ = OpenChatFileLocallyAsync(message);
    }

    private async Task OpenChatFileLocallyAsync(ChatMessage message)
    {
        try
        {
            SetContactAdminStatus("正在下载文件...");
            var localPath = await _chatAttachmentDownloadService.EnsureDownloadedAsync(message);
            if (string.IsNullOrWhiteSpace(localPath) || !File.Exists(localPath))
            {
                SetContactAdminStatus("文件下载失败，请稍后重试。", isError: true);
                return;
            }

            _chatFileLocalPaths[message.Id] = localPath;
            SetContactAdminStatus("");
            LaunchLocalFile(localPath);
        }
        catch (Exception ex)
        {
            SetContactAdminStatus($"文件打开失败：{ex.Message}", isError: true);
            DiagnosticLogService.Warning($"Failed to open chat file locally: {ex.Message}");
        }
    }

    private static void LaunchLocalFile(string path)
    {
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private void AttachChatMessageContextMenu(FrameworkElement target, ChatMessage message, bool copyImage, bool allowSaveAs = false)
    {
        var menu = new System.Windows.Controls.ContextMenu
        {
            Style = (Style)FindResource("ChatContextMenuStyle")
        };
        var copyItem = new System.Windows.Controls.MenuItem
        {
            Header = "复制",
            Style = (Style)FindResource("ChatContextMenuItemStyle")
        };
        copyItem.Click += (_, _) =>
        {
            if (copyImage)
            {
                CopyChatImageToClipboard(message);
                return;
            }

            var text = ChatMessageHelper.GetCopyableText(message);
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            Clipboard.SetText(text);
        };
        menu.Items.Add(copyItem);

        if (allowSaveAs)
        {
            menu.Items.Add(new Separator());
            var saveAsItem = new System.Windows.Controls.MenuItem
            {
                Header = "另存为",
                Style = (Style)FindResource("ChatContextMenuItemStyle")
            };
            saveAsItem.Click += (_, _) => SaveChatAttachmentAs(message);
            menu.Items.Add(saveAsItem);
        }

        target.ContextMenu = menu;
    }

    private void SaveChatAttachmentAs(ChatMessage message)
    {
        var isImage = ChatMessageHelper.IsImageAttachment(message);
        var dialog = new SaveFileDialog
        {
            Title = "另存为",
            FileName = GetChatAttachmentSaveFileName(message),
            Filter = isImage
                ? "PNG 图片|*.png|JPEG 图片|*.jpg;*.jpeg|WebP 图片|*.webp|BMP 图片|*.bmp|所有文件|*.*"
                : "所有文件|*.*"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        _ = SaveChatAttachmentToPathAsync(message, dialog.FileName);
    }

    private static string GetChatAttachmentSaveFileName(ChatMessage message)
    {
        if (!string.IsNullOrWhiteSpace(message.FileName))
        {
            return Path.GetFileName(message.FileName.Trim());
        }

        return ChatMessageHelper.IsImageAttachment(message)
            ? $"image-{message.Id}.png"
            : $"file-{message.Id}";
    }

    private async Task SaveChatAttachmentToPathAsync(ChatMessage message, string targetPath)
    {
        try
        {
            if (ChatMessageHelper.IsImageAttachment(message))
            {
                await SaveChatImageToPathAsync(message, targetPath);
            }
            else
            {
                await SaveChatFileToPathAsync(message, targetPath);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"保存失败：{ex.Message}", "Nexora", MessageBoxButton.OK, MessageBoxImage.Warning);
            DiagnosticLogService.Warning($"Failed to save chat attachment: {ex.Message}");
        }
    }

    private async Task SaveChatImageToPathAsync(ChatMessage message, string targetPath)
    {
        var useRawDownload = Path.GetExtension(targetPath).Equals(".webp", StringComparison.OrdinalIgnoreCase);
        if (!useRawDownload && !string.IsNullOrWhiteSpace(message.FileUrl))
        {
            if (AvatarImageLoader.TryLoadLocal(message.FileUrl, out var localBitmap) && localBitmap is not null)
            {
                SaveBitmapSourceToFile(localBitmap, targetPath);
                return;
            }

            var bitmap = await AvatarImageLoader.LoadBitmapAsync(message.FileUrl);
            if (bitmap is not null)
            {
                SaveBitmapSourceToFile(bitmap, targetPath);
                return;
            }
        }

        if (string.IsNullOrWhiteSpace(message.FileUrl))
        {
            throw new InvalidOperationException("无法获取图片地址。");
        }

        using var response = await DirectHttpClientFactory.Shared.GetAsync(message.FileUrl.Trim());
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync();
        await using var target = File.Create(targetPath);
        await source.CopyToAsync(target);
    }

    private async Task SaveChatFileToPathAsync(ChatMessage message, string targetPath)
    {
        var sourcePath = await GetChatAttachmentLocalPathAsync(message);
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            throw new InvalidOperationException("文件尚未下载完成，请稍后再试。");
        }

        var directory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.Copy(sourcePath, targetPath, overwrite: true);
    }

    private async Task<string?> GetChatAttachmentLocalPathAsync(ChatMessage message)
    {
        if (_chatFileLocalPaths.TryGetValue(message.Id, out var cachedPath) && File.Exists(cachedPath))
        {
            return cachedPath;
        }

        var expectedPath = _chatAttachmentDownloadService.GetLocalPath(message);
        if (File.Exists(expectedPath))
        {
            _chatFileLocalPaths[message.Id] = expectedPath;
            return expectedPath;
        }

        var downloadedPath = await _chatAttachmentDownloadService.EnsureDownloadedAsync(message);
        if (!string.IsNullOrWhiteSpace(downloadedPath) && File.Exists(downloadedPath))
        {
            _chatFileLocalPaths[message.Id] = downloadedPath;
        }

        return downloadedPath;
    }

    private static void SaveBitmapSourceToFile(BitmapSource bitmap, string targetPath)
    {
        BitmapEncoder encoder = Path.GetExtension(targetPath).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => new JpegBitmapEncoder(),
            ".bmp" => new BmpBitmapEncoder(),
            ".gif" => new GifBitmapEncoder(),
            ".webp" or ".png" or _ => new PngBitmapEncoder()
        };

        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var directory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var stream = File.Create(targetPath);
        encoder.Save(stream);
    }

    private void CopyChatImageToClipboard(ChatMessage message)
    {
        _ = CopyChatImageToClipboardAsync(message);
    }

    private async Task CopyChatImageToClipboardAsync(ChatMessage message)
    {
        BitmapImage? bitmap = null;
        if (!string.IsNullOrWhiteSpace(message.FileUrl))
        {
            if (AvatarImageLoader.TryLoadLocal(message.FileUrl, out var localBitmap))
            {
                bitmap = localBitmap;
            }
            else
            {
                bitmap = await AvatarImageLoader.LoadBitmapAsync(message.FileUrl);
            }
        }

        if (bitmap is not null)
        {
            await Dispatcher.InvokeAsync(() => Clipboard.SetImage(bitmap));
            return;
        }

        var text = ChatMessageHelper.GetCopyableText(message);
        if (!string.IsNullOrWhiteSpace(text))
        {
            await Dispatcher.InvokeAsync(() => Clipboard.SetText(text));
        }
    }

    private bool ShowChatImagePreview(ChatMessage message)
    {
        if (string.IsNullOrWhiteSpace(message.FileUrl))
        {
            return false;
        }

        _ = ShowChatImagePreviewAsync(message);
        return true;
    }

    private async Task ShowChatImagePreviewAsync(ChatMessage message)
    {
        BitmapImage? bitmap = null;
        var fileUrl = message.FileUrl ?? "";
        if (AvatarImageLoader.TryLoadLocal(fileUrl, out var localBitmap))
        {
            bitmap = localBitmap;
        }
        else
        {
            bitmap = await AvatarImageLoader.LoadBitmapAsync(fileUrl);
        }

        if (bitmap is null)
        {
            return;
        }

        await Dispatcher.InvokeAsync(() =>
        {
            var dialog = new ChatImagePreviewDialog(bitmap)
            {
                Owner = this
            };
            dialog.ShowDialog();
        });
    }

    private static void OpenChatAttachmentUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url.Trim()) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Warning($"Failed to open chat attachment: {ex.Message}");
        }
    }

    private UIElement CreateChatAvatar(bool isAdmin)
    {
        var host = new Border
        {
            Width = 40,
            Height = 40,
            CornerRadius = new CornerRadius(12),
            Background = isAdmin
                ? new SolidColorBrush(Color.FromRgb(239, 246, 255))
                : new SolidColorBrush(Color.FromRgb(220, 252, 231)),
            BorderBrush = new SolidColorBrush(isAdmin ? Color.FromRgb(191, 219, 254) : Color.FromRgb(134, 239, 172)),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Top,
            ClipToBounds = true
        };

        host.Child = new TextBlock
        {
            Text = isAdmin ? "管" : GetUserAvatarFallbackInitial(),
            FontSize = 16,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(isAdmin ? Color.FromRgb(29, 78, 216) : Color.FromRgb(22, 101, 52)),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var avatarUrl = AvatarUrlHelper.ResolveChatAvatarUrl(isAdmin, _authService.CurrentAvatarUrl);
        if (!string.IsNullOrWhiteSpace(avatarUrl))
        {
            _ = LoadChatAvatarAsync(host, avatarUrl);
        }

        return host;
    }

    private async Task LoadChatAvatarAsync(Border host, string avatarUrl)
    {
        BitmapImage? bitmap = null;
        if (AvatarImageLoader.TryLoadLocal(avatarUrl, out var localBitmap))
        {
            bitmap = localBitmap;
        }
        else
        {
            bitmap = await AvatarImageLoader.LoadBitmapAsync(avatarUrl);
        }

        if (bitmap is null)
        {
            return;
        }

        if (!host.CheckAccess())
        {
            await host.Dispatcher.InvokeAsync(() => ApplyChatAvatarImage(host, bitmap));
            return;
        }

        ApplyChatAvatarImage(host, bitmap);
    }

    private static void ApplyChatAvatarImage(Border host, BitmapImage bitmap)
    {
        host.Child = new System.Windows.Controls.Image
        {
            Source = bitmap,
            Stretch = Stretch.UniformToFill,
            Width = 40,
            Height = 40
        };
    }

    private string GetUserDisplayName()
    {
        var nickname = _authService.CurrentNickname;
        return string.IsNullOrWhiteSpace(nickname) ? "我" : nickname.Trim();
    }

    private string GetUserAvatarFallbackInitial()
    {
        var nickname = _authService.CurrentNickname;
        if (!string.IsNullOrWhiteSpace(nickname))
        {
            return nickname.Trim()[0].ToString();
        }

        return "我";
    }

    private void ChatMessagesScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer)
        {
            return;
        }

        e.Handled = true;
        var nextOffset = scrollViewer.VerticalOffset - e.Delta;
        if (nextOffset < 0)
        {
            nextOffset = 0;
        }
        else if (nextOffset > scrollViewer.ScrollableHeight)
        {
            nextOffset = scrollViewer.ScrollableHeight;
        }

        scrollViewer.ScrollToVerticalOffset(nextOffset);
    }

    private void ScrollChatToEnd(bool force = false)
    {
        if (ChatMessagesScroll is null)
        {
            return;
        }

        void DoScroll()
        {
            ChatMessagesScroll.UpdateLayout();
            ChatMessagesScroll.ScrollToEnd();
        }

        ChatMessagesScroll.Dispatcher.BeginInvoke(DoScroll, DispatcherPriority.Loaded);
        if (force)
        {
            ChatMessagesScroll.Dispatcher.BeginInvoke(DoScroll, DispatcherPriority.ApplicationIdle);
            ChatMessagesScroll.Dispatcher.BeginInvoke(DoScroll, DispatcherPriority.Render);
        }
    }

    private void ShowChatMessageToast(ChatMessage message)
    {
        if (ChatToastPanel is null || ChatToastBodyText is null)
        {
            return;
        }

        ChatToastTitleText.Text = "管理员新消息";
        ChatToastBodyText.Text = TruncateAnnouncementPreview(ChatMessageHelper.GetPreviewText(message));
        ChatToastPanel.Visibility = Visibility.Visible;

        var animation = new DoubleAnimation
        {
            From = -380,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(280),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        ChatToastTransform.BeginAnimation(TranslateTransform.XProperty, animation);

        _chatToastHideTimer.Stop();
        _chatToastHideTimer.Start();
    }

    private void HideChatMessageToast(bool animate = true)
    {
        if (ChatToastPanel is null)
        {
            return;
        }

        _chatToastHideTimer.Stop();
        if (!animate)
        {
            ChatToastPanel.Visibility = Visibility.Collapsed;
            ChatToastTransform.X = -380;
            return;
        }

        var animation = new DoubleAnimation
        {
            To = -380,
            Duration = TimeSpan.FromMilliseconds(220),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };
        animation.Completed += (_, _) =>
        {
            ChatToastPanel.Visibility = Visibility.Collapsed;
        };
        ChatToastTransform.BeginAnimation(TranslateTransform.XProperty, animation);
    }

    private void ChatToastHideTimer_Tick(object? sender, EventArgs e)
    {
        HideChatMessageToast();
    }

    private void ChatToastCloseButton_Click(object sender, RoutedEventArgs e)
    {
        HideChatMessageToast();
    }

    private void ChatToastPanel_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        HideChatMessageToast();
        ShowContactAdminPage();
        ScrollChatToEnd();
    }

    private void SetContactAdminStatus(string message, bool isError = false)
    {
        if (ContactAdminStatusText is null)
        {
            return;
        }

        ContactAdminStatusText.Text = message;
        ContactAdminStatusText.Foreground = isError
            ? (System.Windows.Media.Brush)FindResource("RedBrush")
            : (System.Windows.Media.Brush)FindResource("MutedBrush");
        ContactAdminStatusText.Visibility = string.IsNullOrWhiteSpace(message)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private async void SendChatMessageButton_Click(object sender, RoutedEventArgs e)
    {
        await SendChatMessageAsync();
    }

    private void ChatAttachButton_Click(object sender, RoutedEventArgs e)
    {
        if (ChatAttachPopup is null || ChatAttachButton is null)
        {
            return;
        }

        ChatAttachPopup.PlacementTarget = ChatAttachButton;
        ChatAttachPopup.IsOpen = true;
    }

    private void ChatAttachImageMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (ChatAttachPopup is not null)
        {
            ChatAttachPopup.IsOpen = false;
        }

        var dialog = new OpenFileDialog
        {
            Title = "选择图片",
            Filter = "图片文件|*.jpg;*.jpeg;*.png;*.webp;*.gif|所有文件|*.*"
        };
        if (dialog.ShowDialog() == true)
        {
            SetPendingChatAttachment(dialog.FileName);
        }
    }

    private void ChatAttachFileMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (ChatAttachPopup is not null)
        {
            ChatAttachPopup.IsOpen = false;
        }

        var dialog = new OpenFileDialog
        {
            Title = "选择文件",
            Filter = "所有文件|*.*"
        };
        if (dialog.ShowDialog() == true)
        {
            SetPendingChatAttachment(dialog.FileName);
        }
    }

    private void ChatMessagesArea_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        if (!_authService.IsAuthenticated)
        {
            e.Effects = System.Windows.DragDropEffects.None;
            e.Handled = true;
            return;
        }

        if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
        {
            e.Effects = System.Windows.DragDropEffects.Copy;
            e.Handled = true;
            return;
        }

        e.Effects = System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    private void ChatMessagesArea_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (!_authService.IsAuthenticated || !e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
        {
            return;
        }

        if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is not string[] files || files.Length == 0)
        {
            return;
        }

        e.Handled = true;
        SetPendingChatAttachment(files[0]);
    }

    private void ChatInputBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.V || Keyboard.Modifiers != ModifierKeys.Control || !Clipboard.ContainsImage())
        {
            return;
        }

        e.Handled = true;
        HandleClipboardImagePaste();
    }

    private void HandleClipboardImagePaste()
    {
        var tempPath = SaveClipboardImageToTempFile();
        if (string.IsNullOrWhiteSpace(tempPath))
        {
            SetContactAdminStatus("无法读取剪贴板图片。", isError: true);
            return;
        }

        SetPendingChatAttachment(tempPath);
    }

    private static string? SaveClipboardImageToTempFile()
    {
        if (!Clipboard.ContainsImage())
        {
            return null;
        }

        var image = Clipboard.GetImage();
        if (image is null)
        {
            return null;
        }

        var path = Path.Combine(Path.GetTempPath(), $"nexora-chat-{Guid.NewGuid():N}.png");
        using var stream = File.Create(path);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        encoder.Save(stream);
        return path;
    }

    private string? ValidateChatAttachmentPath(string filePath)
    {
        if (ChatMessageHelper.IsBlockedExtension(filePath))
        {
            return "不允许上传该类型文件。";
        }

        if (!File.Exists(filePath))
        {
            return "所选文件不存在。";
        }

        var fileInfo = new FileInfo(filePath);
        if (fileInfo.Length > 500L * 1024 * 1024)
        {
            return "文件大小不能超过 500MB。";
        }

        return null;
    }

    private void ClearChatAttachmentButton_Click(object sender, RoutedEventArgs e)
    {
        ClearPendingChatAttachment();
    }

    private void SetPendingChatAttachment(string filePath)
    {
        var validationError = ValidateChatAttachmentPath(filePath);
        if (validationError is not null)
        {
            SetContactAdminStatus(validationError, isError: true);
            return;
        }

        _pendingChatAttachmentPath = filePath;
        UpdatePendingChatAttachmentUi();
        SetContactAdminStatus("");
    }

    private void ClearPendingChatAttachment()
    {
        _pendingChatAttachmentPath = null;
        UpdatePendingChatAttachmentUi();
    }

    private void UpdatePendingChatAttachmentUi()
    {
        if (ChatPendingAttachmentPanel is null || ChatPendingAttachmentText is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_pendingChatAttachmentPath))
        {
            ChatPendingAttachmentPanel.Visibility = Visibility.Collapsed;
            ChatPendingAttachmentText.Text = "";
            if (ChatPendingAttachmentImageHost is not null)
            {
                ChatPendingAttachmentImageHost.Visibility = Visibility.Collapsed;
            }

            if (ChatPendingAttachmentImage is not null)
            {
                ChatPendingAttachmentImage.Source = null;
            }

            return;
        }

        var fileInfo = new FileInfo(_pendingChatAttachmentPath);
        var isImage = ChatMessageHelper.IsImageFilePath(_pendingChatAttachmentPath);
        if (isImage && ChatPendingAttachmentImageHost is not null && ChatPendingAttachmentImage is not null)
        {
            if (TryLoadAvatarBitmap(_pendingChatAttachmentPath, out var previewBitmap))
            {
                ChatPendingAttachmentImage.Source = previewBitmap;
                ChatPendingAttachmentImageHost.Visibility = Visibility.Visible;
            }
            else
            {
                ChatPendingAttachmentImage.Source = null;
                ChatPendingAttachmentImageHost.Visibility = Visibility.Collapsed;
            }
        }
        else if (ChatPendingAttachmentImageHost is not null)
        {
            ChatPendingAttachmentImageHost.Visibility = Visibility.Collapsed;
            if (ChatPendingAttachmentImage is not null)
            {
                ChatPendingAttachmentImage.Source = null;
            }
        }

        ChatPendingAttachmentText.Text = isImage
            ? $"待发送图片：{fileInfo.Name}（{ChatMessageHelper.FormatFileSize(fileInfo.Length)}）"
            : $"待发送文件：{fileInfo.Name}（{ChatMessageHelper.FormatFileSize(fileInfo.Length)}）";
        ChatPendingAttachmentPanel.Visibility = Visibility.Visible;
    }

    private async void ChatInputBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            await SendChatMessageAsync();
        }
    }

    private async Task SendChatMessageAsync(bool clearPendingAttachment = true)
    {
        if (!_authService.IsAuthenticated)
        {
            SetContactAdminStatus("请先登录后发送消息。", isError: true);
            return;
        }

        var content = ChatInputBox.Text?.Trim() ?? "";
        var attachmentPath = _pendingChatAttachmentPath;
        if (string.IsNullOrWhiteSpace(content) && string.IsNullOrWhiteSpace(attachmentPath))
        {
            SetContactAdminStatus("消息内容和文件不能同时为空。", isError: true);
            return;
        }

        if (!string.IsNullOrWhiteSpace(content) && content.Length > 2000)
        {
            SetContactAdminStatus("消息内容最多 2000 个字符。", isError: true);
            return;
        }

        if (!string.IsNullOrWhiteSpace(attachmentPath))
        {
            var validationError = ValidateChatAttachmentPath(attachmentPath);
            if (validationError is not null)
            {
                SetContactAdminStatus(validationError, isError: true);
                if (clearPendingAttachment)
                {
                    ClearPendingChatAttachment();
                }

                return;
            }
        }

        if (!await _authService.TryRestoreSessionAsync())
        {
            SetContactAdminStatus("登录已过期，请重新登录。", isError: true);
            UpdateNotificationPagesAuthState();
            return;
        }

        SendChatMessageButton.IsEnabled = false;
        ChatAttachButton.IsEnabled = false;
        SetContactAdminStatus(string.IsNullOrWhiteSpace(attachmentPath) ? "正在发送..." : "正在上传...");
        try
        {
            ApiResult<ChatMessage> result;
            if (!string.IsNullOrWhiteSpace(attachmentPath))
            {
                result = await _authService.ChatApi.SendMixedMessageAsync(
                    string.IsNullOrWhiteSpace(content) ? null : content,
                    attachmentPath);
            }
            else
            {
                result = await _authService.ChatApi.SendMessageAsync(content);
            }

            if (!result.IsSuccess || result.Data is null)
            {
                SetContactAdminStatus(result.Message, isError: true);
                return;
            }

            ChatInputBox.Text = "";
            if (clearPendingAttachment)
            {
                ClearPendingChatAttachment();
            }
            else
            {
                _pendingChatAttachmentPath = null;
                UpdatePendingChatAttachmentUi();
            }

            AppendChatMessage(result.Data);
            SetContactAdminStatus("");
        }
        catch (Exception ex)
        {
            SetContactAdminStatus($"发送失败：{ex.Message}", isError: true);
            DiagnosticLogService.Error("Failed to send chat message.", ex);
        }
        finally
        {
            SendChatMessageButton.IsEnabled = true;
            ChatAttachButton.IsEnabled = true;
        }
    }

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

    private void ShowImportPage()
    {
        ShowPage(ImportPageScroll, ImportNodeNavButton);
    }

    private void SettingsNavButton_Click(object sender, RoutedEventArgs e) => ShowSettingsPage();

    private void ShowSettingsPage()
    {
        SyncRunAtStartupFromSettings();
        SyncAllowLanAccessFromSettings();
        SyncAutoDownloadUpdateFromSettings();
        SyncThemeSettingsUi(ThemeService.ParseAccentColor(_settings.ThemeAccentColor));
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

    private void ShowPage(FrameworkElement page, System.Windows.Controls.Button? activeButton)
    {
        NodePageScroll.Visibility = Visibility.Collapsed;
        NewNodePageScroll.Visibility = Visibility.Collapsed;
        ImportPageScroll.Visibility = Visibility.Collapsed;
        NodeTestPageScroll.Visibility = Visibility.Collapsed;
        AnnouncementPageScroll.Visibility = Visibility.Collapsed;
        ContactAdminPageScroll.Visibility = Visibility.Collapsed;
        VersionUpdatePageScroll.Visibility = Visibility.Collapsed;
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

        foreach (var button in new[] { NodeListNavButton, ImportNodeNavButton, NodeTestNavButton, AnnouncementNavButton, ContactAdminNavButton, VersionUpdateNavButton, SettingsNavButton, LogNavButton, AboutNavButton })
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

    private void NodeTestPageScroll_SizeChanged(object sender, SizeChangedEventArgs e) =>
        ScheduleWebsiteTestResponsiveLayout();

    private void WebsiteTestList_SizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateWebsiteTestResponsiveLayout();

    private void ScheduleWebsiteTestResponsiveLayout()
    {
        Dispatcher.BeginInvoke(UpdateWebsiteTestResponsiveLayout, DispatcherPriority.Loaded);
    }

    private double GetWebsiteTestListWidth()
    {
        if (WebsiteTestList?.Parent is FrameworkElement parent)
        {
            parent.UpdateLayout();
            if (parent.ActualWidth > 0)
            {
                return parent.ActualWidth;
            }
        }

        if (NodeTestPageScroll is null)
        {
            return Math.Max(0, ActualWidth - 330);
        }

        var viewportWidth = NodeTestPageScroll.ViewportWidth > 0
            ? NodeTestPageScroll.ViewportWidth
            : NodeTestPageScroll.ActualWidth;

        return Math.Max(0, viewportWidth - 80);
    }

    private void UpdateWebsiteTestResponsiveLayout()
    {
        if (!_isUiReady || _websiteTests.Count == 0)
        {
            return;
        }

        var availableWidth = GetWebsiteTestListWidth();
        if (availableWidth <= 0)
        {
            return;
        }

        var itemCount = _websiteTests.Count;
        var columns = Math.Max(1, (int)Math.Floor(availableWidth / (WebsiteTestCardMinWidth + WebsiteTestCardGap)));
        columns = Math.Min(columns, itemCount);

        var cardWidth = (availableWidth / columns) - WebsiteTestCardGap;
        while (columns > 1 && cardWidth < WebsiteTestCardMinWidth)
        {
            columns--;
            cardWidth = (availableWidth / columns) - WebsiteTestCardGap;
        }

        cardWidth = Math.Max(WebsiteTestCardAbsoluteMinWidth, cardWidth);

        if (Math.Abs(WebsiteTestCardWidth - cardWidth) > 0.5)
        {
            WebsiteTestCardWidth = cardWidth;
        }
    }

    private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (AboutPageScroll.Visibility == Visibility.Visible)
        {
            UpdateAboutResponsiveLayout(GetAboutContentWidth());
        }

        if (NodeTestPageScroll.Visibility == Visibility.Visible)
        {
            ScheduleWebsiteTestResponsiveLayout();
        }

        ScheduleMainHeaderResponsiveLayout();
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
        if (!_isUiReady || AboutSummaryGrid is null)
        {
            return;
        }

        var stacked = availableWidth < AboutTwoColumnBreakpoint;
        ApplyAboutTwoColumnLayout(AboutSummaryGrid, AboutSummaryLeftPanel, AboutSummaryRightPanel, stacked, leftColumnWeight: 1);
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
            MarkProfileAsLocalManual(profile);

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
            SetInlineImportText(Clipboard.GetText());
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
            SetInlineImportText(await File.ReadAllTextAsync(dialog.FileName));
        }
    }

    private async void InlineImportButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await ImportContentAsync(GetInlineImportText());
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private async Task ImportContentAsync(string content)
    {
        SetInlineImportText(content);
        var result = await SubscriptionImportService.ImportAsync(content);
        await AddImportedProfilesAsync(result);
    }

    private void ConfigureImportPlaceholder()
    {
        const string placeholder = "支持节点链接、订阅地址、Base64 订阅内容与多行批量导入…";
        InlineImportBox.Tag = placeholder;
        if (string.IsNullOrWhiteSpace(InlineImportBox.Text))
        {
            ApplyInlineImportPlaceholder();
        }

        InlineImportBox.GotFocus += (_, _) =>
        {
            if (IsInlineImportShowingPlaceholder())
            {
                InlineImportBox.Text = "";
                InlineImportBox.Foreground = (System.Windows.Media.Brush)FindResource("TextBrush");
            }
        };

        InlineImportBox.LostFocus += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(InlineImportBox.Text))
            {
                ApplyInlineImportPlaceholder();
            }
        };
    }

    private void ApplyInlineImportPlaceholder()
    {
        InlineImportBox.Text = (string)InlineImportBox.Tag;
        InlineImportBox.Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush");
    }

    private bool IsInlineImportShowingPlaceholder() =>
        InlineImportBox.Tag is string placeholder && InlineImportBox.Text == placeholder;

    private string GetInlineImportText() =>
        IsInlineImportShowingPlaceholder() ? "" : InlineImportBox.Text;

    private void SetInlineImportText(string text)
    {
        InlineImportBox.Text = text;
        InlineImportBox.Foreground = (System.Windows.Media.Brush)FindResource("TextBrush");
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

    private async Task AddImportedProfilesAsync(SubscriptionImportResult result)
    {
        var source = RegisterSubscriptionSource(result);
        var subscriptionName = result.SubscriptionName ?? "订阅";
        SubscriptionMetadataHelper.ApplyToProfiles(result, subscriptionName);

        if (source is not null)
        {
            var syncResult = await _authService.SubscriptionSync.SyncImportAsync(result, source, subscriptionName);
            if (!syncResult.Success)
            {
                DiagnosticLogService.Warning($"Subscription sync failed: {syncResult.Message}");
            }
            else if (!string.IsNullOrWhiteSpace(syncResult.SnapshotPath))
            {
                DiagnosticLogService.Info($"Subscription snapshot saved: {syncResult.SnapshotPath}");
            }

            _settingsStore.Save(_settings);
        }

        foreach (var profile in result.Profiles)
        {
            if (source is null)
            {
                MarkProfileAsLocalManual(profile);
            }
            else
            {
                MarkProfileAsLocalSubscription(profile);
            }

            _profiles.Add(profile);
        }

        ApplySubscriptionTrafficInfo(result.TrafficInfo);
        var last = result.Profiles.LastOrDefault();
        SaveProfiles(PreserveActiveProfileId());
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
        RefreshProfilesView();

        if (result.Profiles.Count > 0)
        {
            _ = RunTcpLatencyTestsAsync(result.Profiles, parallel: true);
        }
    }

    private SubscriptionSource? RegisterSubscriptionSource(SubscriptionImportResult result)
    {
        if (string.IsNullOrWhiteSpace(result.SourceUrl) || string.IsNullOrWhiteSpace(result.SubscriptionName))
        {
            return null;
        }

        var sourceKey = LocalSubscriptionHelper.GetLocalSourceKey(result.SubscriptionName);
        _settings.SubscriptionSources.TryGetValue(sourceKey, out var existing);
        var source = new SubscriptionSource
        {
            Url = result.SourceUrl,
            AutoRefreshMinutes = existing?.AutoRefreshMinutes,
            ServerSubscriptionId = null,
            DisplayName = existing?.DisplayName ?? result.SubscriptionName,
            CreatedAtUtc = existing?.CreatedAtUtc ?? DateTime.UtcNow,
            IsLocalOnly = true
        };
        _settings.SubscriptionSources[sourceKey] = source;
        return source;
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
        timer.Tick += async (_, _) => await RefreshSubscriptionAsync(subscriptionName, silent: true);
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
        if (sender is FrameworkElement { Tag: string groupDisplay })
        {
            var scope = LocalSubscriptionHelper.ParseGroupDisplay(groupDisplay);
            if (!scope.IsManual)
            {
                await RefreshSubscriptionAsync(scope);
            }
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

        var groupDisplay = FindSubscriptionGroupName(target);
        var scope = LocalSubscriptionHelper.ParseGroupDisplay(groupDisplay);
        if (scope.IsManual || string.IsNullOrWhiteSpace(scope.Name) && !scope.IsLocal)
        {
            menu.IsOpen = false;
            return;
        }

        _subscriptionContextMenuScope = scope;
    }

    private async void SubscriptionDeleteMenu_Click(object sender, RoutedEventArgs e)
    {
        if (_subscriptionContextMenuScope is { } scope)
        {
            await DeleteSubscriptionAsync(scope);
        }
    }

    private async void SubscriptionRefreshMenu_Click(object sender, RoutedEventArgs e)
    {
        if (_subscriptionContextMenuScope is { } scope)
        {
            await RefreshSubscriptionAsync(scope);
        }
    }

    private void SubscriptionAutoRefreshOff_Click(object sender, RoutedEventArgs e)
    {
        if (_subscriptionContextMenuScope is { } scope)
        {
            StopSubscriptionAutoRefresh(scope.SourceKey);
        }
    }

    private void SubscriptionAutoRefreshPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem { Tag: string tag } ||
            !int.TryParse(tag, out var minutes) ||
            _subscriptionContextMenuScope is not { } scope)
        {
            return;
        }

        StartSubscriptionAutoRefresh(scope.SourceKey, minutes);
    }

    private void SubscriptionAutoRefreshCustom_Click(object sender, RoutedEventArgs e)
    {
        if (_subscriptionContextMenuScope is not { } scope)
        {
            return;
        }

        _settings.SubscriptionSources.TryGetValue(scope.SourceKey, out var source);
        var dialog = new DurationPromptDialog(scope.Name, source?.AutoRefreshMinutes)
        {
            Owner = this
        };

        if (dialog.ShowDialog() == true)
        {
            StartSubscriptionAutoRefresh(scope.SourceKey, dialog.Minutes);
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

    private bool TryGetCloudSubscriptionId(SubscriptionGroupIdentity scope, out int subscriptionId)
    {
        subscriptionId = 0;
        if (!_authService.IsAuthenticated || scope.IsManual || scope.IsLocal)
        {
            return false;
        }

        if (!_settings.SubscriptionSources.TryGetValue(scope.SourceKey, out var source))
        {
            var fromSession = _authService.CurrentSession?.Subscriptions.FirstOrDefault(subscription =>
                string.Equals(subscription.Name, scope.Name, StringComparison.OrdinalIgnoreCase));
            if (fromSession is not null)
            {
                subscriptionId = fromSession.Id;
                return true;
            }

            return false;
        }

        if (source.IsLocalOnly)
        {
            return false;
        }

        _authService.SubscriptionSync.ResolveAndAssignServerSubscriptionId(source, scope.Name);
        if (source.ServerSubscriptionId is int resolvedId && resolvedId > 0)
        {
            subscriptionId = resolvedId;
            return true;
        }

        return false;
    }

    private async Task DeleteSubscriptionAsync(SubscriptionGroupIdentity scope)
    {
        if (scope.IsManual)
        {
            return;
        }

        var sourceKey = scope.SourceKey;
        var isCloudSubscription = TryGetCloudSubscriptionId(scope, out var subscriptionId);
        var nodeCount = GetProfilesForSubscription(scope).Count;
        var displayName = scope.IsLocal
            ? LocalSubscriptionHelper.FormatLocalSubscriptionDisplay(scope.Name)
            : scope.Name;

        var message = isCloudSubscription
            ? $"确定删除订阅「{displayName}」吗？{Environment.NewLine}{Environment.NewLine}" +
              $"将同时删除云端订阅链接及本地 {nodeCount} 个节点。{Environment.NewLine}" +
              "此操作不可恢复，是否继续？"
            : $"确定删除本地订阅「{displayName}」及 {nodeCount} 个节点吗？";

        if (MessageBox.Show(
                message,
                "删除订阅",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        if (isCloudSubscription && subscriptionId <= 0)
        {
            MessageBox.Show(
                "未找到云端订阅 ID，请重新登录后再试。",
                "Nexora",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (isCloudSubscription)
        {
            var deleteResult = await _authService.SubscriptionApi.DeleteAsync(subscriptionId);
            if (!deleteResult.IsSuccess)
            {
                MessageBox.Show(
                    $"云端订阅删除失败：{deleteResult.Message}",
                    "Nexora",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            _authService.RemoveSubscriptionFromSession(subscriptionId);
        }

        StopSubscriptionAutoRefresh(sourceKey, save: false);
        RemoveProfilesForSubscription(scope);
        _settings.SubscriptionSources.Remove(sourceKey);

        var nextActiveId = _profiles.FirstOrDefault(profile => profile.Id == _settings.SelectedProfileId)?.Id
            ?? _profiles.FirstOrDefault()?.Id;
        SaveProfiles(nextActiveId);
        RefreshNodePicker();
        SyncNodePickerDisplay();
        RefreshRegionFilterOptions();
        RefreshSubscriptionFilterOptions();
        ProfilesGrid.Items.Refresh();
        ProfilesGrid.SelectedItem = _profiles.FirstOrDefault(profile => profile.Id == nextActiveId);
        _settingsStore.Save(_settings);

        MessageBox.Show(
            isCloudSubscription
                ? $"订阅「{displayName}」已从云端和本地删除。"
                : $"订阅「{displayName}」及本地节点已删除。",
            "Nexora",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private Task RefreshSubscriptionAsync(string sourceKey, bool silent = false) =>
        RefreshSubscriptionAsync(IdentityFromSourceKey(sourceKey), silent);

    private async Task RefreshSubscriptionAsync(SubscriptionGroupIdentity scope, bool silent = false)
    {
        if (scope.IsManual)
        {
            return;
        }

        var sourceKey = scope.SourceKey;
        var subscriptionName = scope.Name;

        if (silent && IsSubscriptionTrafficExhausted(sourceKey))
        {
            return;
        }

        if (!_settings.SubscriptionSources.TryGetValue(sourceKey, out var source) ||
            string.IsNullOrWhiteSpace(source.Url))
        {
            if (!silent)
            {
                MessageBox.Show("该订阅没有保存原始地址，请重新导入订阅链接。", "Nexora", MessageBoxButton.OK, MessageBoxImage.Information);
            }

            return;
        }

        var requiresAuth = !scope.IsLocal &&
                           (source.ServerSubscriptionId is > 0 ||
                           _profiles.Any(profile =>
                               profile.IsCloudManaged &&
                               string.Equals(profile.SubscriptionName, subscriptionName, StringComparison.OrdinalIgnoreCase)));
        if (requiresAuth)
        {
            if (!await _authService.TryRestoreSessionAsync())
            {
                if (!silent)
                {
                    MessageBox.Show("登录已过期，请重新登录后再刷新订阅。", "Nexora", MessageBoxButton.OK, MessageBoxImage.Information);
                }

                return;
            }
        }

        List<VmessProfile>? profilesToTest = null;
        try
        {
            if (requiresAuth)
            {
                _authService.SubscriptionSync.ResolveAndAssignServerSubscriptionId(source, subscriptionName);
            }

            var result = await SubscriptionImportService.ImportAsync(source.Url);
            source.DisplayName = RemoveInvalidSubscriptionSuffix(source.DisplayName ?? subscriptionName);

            if (SubscriptionTrafficHelper.IsTrafficExhausted(result.TrafficInfo) ||
                SubscriptionTrafficHelper.AreProfilesTrafficExhausted(result.Profiles))
            {
                ApplySubscriptionTrafficInfo(result.TrafficInfo);
                SetSubscriptionTrafficExhausted(sourceKey);
                if (!silent)
                {
                    ShowSubscriptionTrafficExhaustedDialog(subscriptionName);
                }

                return;
            }

            ClearSubscriptionTrafficExhausted(sourceKey);
            var previousActive = GetSelectedProfileOrNull();
            bool? cloudSyncSuccess = null;
            string? cloudSyncMessage = null;
            SubscriptionMergeResult mergeResult;

            _subscriptionUpdateBatchActive++;
            EnterProfilesViewFrozenState();
            try
            {
                SubscriptionMetadataHelper.ApplyToProfiles(
                    new SubscriptionImportResult
                    {
                        Profiles = result.Profiles,
                        TrafficInfo = result.TrafficInfo,
                        SourceUrl = source.Url,
                        SubscriptionName = subscriptionName
                    },
                    subscriptionName);

                mergeResult = MergeSubscriptionProfiles(scope, result.Profiles);
                ApplySubscriptionTrafficInfo(result.TrafficInfo);

                if (requiresAuth)
                {
                    var subscriptionProfiles = GetProfilesForSubscription(scope);
                    var syncResult = await _authService.SubscriptionSync.SyncRefreshAsync(
                        new SubscriptionImportResult
                        {
                            Profiles = subscriptionProfiles,
                            TrafficInfo = result.TrafficInfo,
                            SourceUrl = source.Url,
                            SubscriptionName = subscriptionName
                        },
                        source,
                        subscriptionName);

                    foreach (var profile in subscriptionProfiles)
                    {
                        MarkProfileAsCloudManaged(profile);
                    }

                    cloudSyncSuccess = syncResult.Success;
                    cloudSyncMessage = syncResult.Message;
                    if (!syncResult.Success)
                    {
                        DiagnosticLogService.Warning($"Subscription refresh sync failed: {syncResult.Message}");
                    }
                    else
                    {
                        DiagnosticLogService.Info(
                            $"Subscription refreshed and synced to server. id={syncResult.ServerSubscriptionId}, message={syncResult.Message}");
                    }
                }
                else
                {
                    foreach (var profile in GetProfilesForSubscription(scope))
                    {
                        MarkProfileAsLocalSubscription(profile);
                    }
                }

                _settingsStore.Save(_settings);

                SaveProfiles(PreserveActiveProfileId(previousActive));
                RefreshNodePicker();
                SyncNodePickerDisplay();
                RefreshRegionFilterOptions();
                RefreshSubscriptionFilterOptions();
                RefreshProfilesView();

                profilesToTest = GetProfilesForSubscription(scope);

                if (!silent)
                {
                    ShowSubscriptionRefreshSummary(
                        subscriptionName,
                        mergeResult,
                        profilesToTest.Count,
                        cloudSyncSuccess,
                        cloudSyncMessage);
                }
            }
            finally
            {
                ExitProfilesViewFrozenState();
                _subscriptionUpdateBatchActive--;
            }

            ScheduleRegionEnrichment(GetProfilesForSubscription(scope));
        }
        catch (Exception ex)
        {
            if (ShouldTreatRefreshFailureAsTrafficExhausted(scope, ex.Message, ex))
            {
                SetSubscriptionTrafficExhausted(sourceKey);
                if (!silent)
                {
                    ShowSubscriptionTrafficExhaustedDialog(subscriptionName);
                }

                return;
            }

            if (SubscriptionTrafficHelper.IsTransientNetworkError(ex.Message, ex))
            {
                DiagnosticLogService.Warning($"Subscription refresh failed for \"{subscriptionName}\" (transient): {ex.Message}");
                if (!silent)
                {
                    MessageBox.Show(
                        $"订阅「{subscriptionName}」暂时无法刷新，已保留现有节点：{ex.Message}",
                        "Nexora",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }

                return;
            }

            if (!silent && !scope.IsLocal)
            {
                MarkSubscriptionSourceInvalid(subscriptionName);
                RemoveProfilesForSubscription(scope);
                var nextActiveId = _profiles.FirstOrDefault(profile => profile.Id == _settings.SelectedProfileId)?.Id
                    ?? _profiles.FirstOrDefault()?.Id;
                SaveProfiles(nextActiveId);
                RefreshNodePicker();
                SyncNodePickerDisplay();
                RefreshRegionFilterOptions();
                RefreshSubscriptionFilterOptions();
                ProfilesGrid.Items.Refresh();
                ProfilesGrid.SelectedItem = _profiles.FirstOrDefault(profile => profile.Id == nextActiveId);
                ShowError(ex);
            }
            else if (!silent)
            {
                ShowError(ex);
            }

            DiagnosticLogService.Warning($"Subscription refresh failed for \"{subscriptionName}\": {ex.Message}");
            return;
        }

        if (profilesToTest is { Count: > 0 })
        {
            await RunTcpLatencyTestsAsync(profilesToTest, parallel: true);
        }

        ApplyActiveProfileSelection(autoSelectIfMissing: GetSelectedProfileOrNull() is null, save: true);
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

    private void CtxExportNode_Click(object sender, RoutedEventArgs e)
    {
        var profile = ProfilesGrid.SelectedItem as VmessProfile;
        if (profile is null)
        {
            MessageBox.Show("请先选择要导出的节点。", "Nexora", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        OpenExportNodeDialog(profile);
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

        _latencyTestBatchActive++;
        FreezeProfilesViewForLatencyTest();
        SetLatencyTestingEnabled(false);

        try
        {
            IReadOnlyList<(VmessProfile Profile, int? Latency)> results;
            if (parallel)
            {
                var tasks = profiles.Select(profile => MeasureProfileLatencyAsync(profile, cancellationToken));
                results = await Task.WhenAll(tasks);
            }
            else
            {
                var sequentialResults = new List<(VmessProfile Profile, int? Latency)>(profiles.Count);
                foreach (var profile in profiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    sequentialResults.Add(await MeasureProfileLatencyAsync(profile, cancellationToken));
                }

                results = sequentialResults;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            await Dispatcher.InvokeAsync(() => ApplyLatencyResultsOnUiThread(results), DispatcherPriority.Send);
            await TryAutoSwitchFromTimedOutCurrentNodeAsync();
            await Dispatcher.InvokeAsync(() => UpdateTopBarLatencyForProfile(GetSelectedProfileOrNull()), DispatcherPriority.Send);
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
            _latencyTestBatchActive--;
            ExitProfilesViewFrozenState();
            SetLatencyTestingEnabled(true);
        }
    }

    private static async Task<(VmessProfile Profile, int? Latency)> MeasureProfileLatencyAsync(
        VmessProfile profile,
        CancellationToken cancellationToken)
    {
        if (profile.IsExpired)
        {
            return (profile, null);
        }

        var latency = await LatencyTestService.MeasureTcpAsync(
            profile.Address,
            profile.Port,
            cancellationToken: cancellationToken);
        return (profile, cancellationToken.IsCancellationRequested ? null : latency);
    }

    private void UpdateTopBarLatencyForProfile(VmessProfile? profile)
    {
        if (profile is null)
        {
            _topBarLatencyProfileId = null;
            _topBarLastLatencyMs = null;
            _topBarShowingTimeout = false;
            CurrentTcpLatencyText.Text = TopBarLatencyPlaceholder;
            return;
        }

        UpdateTopBarLatencyDisplay(profile, force: true);
    }

    private void EnterProfilesViewFrozenState()
    {
        if (_profilesViewFreezeDepth++ > 0)
        {
            return;
        }

        if (_profilesView is not ListCollectionView listView)
        {
            return;
        }

        _profilesViewLiveSorting = listView.IsLiveSorting ?? false;
        _profilesViewLiveFiltering = listView.IsLiveFiltering ?? false;
        listView.IsLiveSorting = false;
        listView.IsLiveFiltering = false;
        _frozenProfilesSort = listView.SortDescriptions
            .Select(sort => new SortDescription(sort.PropertyName, sort.Direction))
            .ToList();
        listView.SortDescriptions.Clear();
    }

    private void ExitProfilesViewFrozenState()
    {
        if (_profilesViewFreezeDepth <= 0)
        {
            return;
        }

        if (--_profilesViewFreezeDepth > 0)
        {
            return;
        }

        UnfreezeProfilesViewAfterLatencyTest();
        RefreshProfilesView();
    }

    private void FreezeProfilesViewForLatencyTest() => EnterProfilesViewFrozenState();

    private void UnfreezeProfilesViewAfterLatencyTest()
    {
        if (_profilesView is not ListCollectionView listView)
        {
            return;
        }

        listView.SortDescriptions.Clear();
        foreach (var sort in _frozenProfilesSort)
        {
            listView.SortDescriptions.Add(sort);
        }

        listView.IsLiveFiltering = _profilesViewLiveFiltering;
        listView.IsLiveSorting = SortsByLatency(_frozenProfilesSort)
            ? false
            : _profilesViewLiveSorting;
    }

    private void ApplyLatencyResultsOnUiThread(IReadOnlyList<(VmessProfile Profile, int? Latency)> results)
    {
        if (_profilesView is ListCollectionView listView)
        {
            using (listView.DeferRefresh())
            {
                foreach (var (profile, latency) in results)
                {
                    profile.TryApplyLatencyResult(latency);
                }
            }

            return;
        }

        foreach (var (profile, latency) in results)
        {
            profile.TryApplyLatencyResult(latency);
        }
    }

    private async Task TryAutoSwitchFromTimedOutCurrentNodeAsync()
    {
        var current = GetSelectedProfileOrNull();
        if (current is null || !IsUnavailableProfile(current))
        {
            return;
        }

        var alternative = FindBestAvailableProfile(excludeId: current.Id);
        if (alternative is null)
        {
            DiagnosticLogService.Info($"Current node \"{current.DisplayName}\" timed out; no other available node to switch.");
            return;
        }

        DiagnosticLogService.Info(
            $"Current node \"{current.DisplayName}\" timed out; switching to \"{alternative.DisplayName}\" ({alternative.TcpLatencyMs} ms).");
        SaveProfiles(alternative.Id);
        ProfilesGrid.SelectedItem = alternative;
        UpdateNodeAddressInStatusBar(alternative);
        UpdateTopBarLatencyForProfile(alternative);
        if (_coreService.IsRunning)
        {
            await RestartCoreAsync();
        }

        UpdateTrayStatus();
    }

    private VmessProfile? FindBestAvailableProfile(string? excludeId = null)
    {
        return _profiles
            .Where(profile => excludeId is null || profile.Id != excludeId)
            .Where(profile => !profile.IsExpired)
            .Where(profile => profile.TcpLatencyMs is not null)
            .OrderBy(profile => profile.TcpLatencyMs)
            .FirstOrDefault();
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

    private string? PreserveActiveProfileId(VmessProfile? referenceActive = null)
    {
        referenceActive ??= GetSelectedProfileOrNull();
        var currentId = _settings.SelectedProfileId;
        if (!string.IsNullOrWhiteSpace(currentId) && _profiles.Any(profile => profile.Id == currentId))
        {
            return currentId;
        }

        if (referenceActive is not null)
        {
            var referenceKey = BuildProfileKey(referenceActive);
            var matched = _profiles.FirstOrDefault(profile => BuildProfileKey(profile) == referenceKey);
            if (matched is not null)
            {
                return matched.Id;
            }
        }

        return currentId;
    }

    private string? EnsureActiveProfileId()
    {
        var preserved = PreserveActiveProfileId();
        if (!string.IsNullOrWhiteSpace(preserved) && _profiles.Any(profile => profile.Id == preserved))
        {
            return preserved;
        }

        return FindBestAvailableProfile()?.Id
            ?? _profiles
                .Where(profile => !profile.IsExpired)
                .OrderBy(profile => profile.TcpLatencyMs ?? int.MaxValue)
                .ThenBy(profile => profile.DisplayName, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault()?.Id
            ?? _profiles.FirstOrDefault()?.Id;
    }

    private void ApplyActiveProfileSelection(bool autoSelectIfMissing = false, bool save = false, VmessProfile? referenceActive = null)
    {
        var activeId = autoSelectIfMissing
            ? EnsureActiveProfileId()
            : PreserveActiveProfileId(referenceActive);

        if (string.IsNullOrWhiteSpace(activeId))
        {
            UpdateActiveProfileMarkers(null);
            UpdateNodeStatusBar(null);
            ProfilesGrid.SelectedItem = null;
            return;
        }

        if (save)
        {
            SaveProfiles(activeId);
        }
        else
        {
            _settings.SelectedProfileId = activeId;
            UpdateActiveProfileMarkers(activeId);
        }

        var active = GetSelectedProfileOrNull();
        ProfilesGrid.SelectedItem = active;
        UpdateNodeStatusBar(active);
    }

    private void SaveProfiles(string? selectedProfileId)
    {
        _settings.Profiles = _profiles.ToList();
        _settings.SelectedProfileId = selectedProfileId;
        _settingsStore.Save(_settings);
        UpdateActiveProfileMarkers(selectedProfileId);
        StopProxyIfNoProfiles();
    }

    private void StopProxyIfNoProfiles()
    {
        if (_profiles.Count != 0 || !_coreService.IsRunning)
        {
            return;
        }

        StopProxy();
    }

    private sealed class SubscriptionMergeResult
    {
        public int AddedCount { get; set; }
        public int UpdatedCount { get; set; }
        public int RemovedCount { get; set; }
    }

    private SubscriptionMergeResult MergeSubscriptionProfiles(
        SubscriptionGroupIdentity scope,
        IReadOnlyList<VmessProfile> importedProfiles)
    {
        var mergeResult = new SubscriptionMergeResult();
        var existingProfiles = GetProfilesForSubscription(scope);
        var existingByKey = existingProfiles.ToDictionary(BuildProfileKey, profile => profile);
        var importedByKey = new Dictionary<string, VmessProfile>(importedProfiles.Count);
        foreach (var imported in importedProfiles)
        {
            importedByKey[BuildProfileKey(imported)] = imported;
        }

        foreach (var (key, imported) in importedByKey)
        {
            imported.SubscriptionUpdatedAt = DateTime.Now;
            if (existingByKey.TryGetValue(key, out var existing))
            {
                CopyProfile(imported, existing);
                if (scope.IsLocal)
                {
                    MarkProfileAsLocalSubscription(existing);
                }
                else
                {
                    MarkProfileAsCloudManaged(existing);
                }

                existing.NotifyListDisplayChanged();
                mergeResult.UpdatedCount++;
                continue;
            }

            imported.UpdatedAt = DateTime.Now;
            if (scope.IsLocal)
            {
                MarkProfileAsLocalSubscription(imported);
            }
            else
            {
                MarkProfileAsCloudManaged(imported);
            }

            _profiles.Add(imported);
            mergeResult.AddedCount++;
        }

        foreach (var existing in existingProfiles)
        {
            if (importedByKey.ContainsKey(BuildProfileKey(existing)))
            {
                continue;
            }

            _profiles.Remove(existing);
            mergeResult.RemovedCount++;
        }

        return mergeResult;
    }

    private void ShowSubscriptionRefreshSummary(
        string subscriptionName,
        SubscriptionMergeResult mergeResult,
        int latencyTestedCount,
        bool? cloudSyncSuccess,
        string? cloudSyncMessage)
    {
        var details = new List<string>
        {
            $"新增节点：{mergeResult.AddedCount}",
            $"更新节点：{mergeResult.UpdatedCount}",
            $"移除节点：{mergeResult.RemovedCount}"
        };

        if (latencyTestedCount > 0)
        {
            details.Add($"测速节点：{latencyTestedCount}");
        }

        if (cloudSyncSuccess == true)
        {
            details.Add("已同步到云端。");
        }
        else if (cloudSyncSuccess == false)
        {
            details.Add($"同步到云端失败：{cloudSyncMessage}");
        }
        else
        {
            details.Add("订阅已本地刷新完成。");
        }

        var kind = cloudSyncSuccess == false ? ThemedMessageKind.Warning : ThemedMessageKind.Information;
        ThemedMessageDialog.Show(this, $"订阅「{subscriptionName}」刷新完成。", details, kind);
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
        target.Flow = source.Flow;
        target.RealityPublicKey = source.RealityPublicKey;
        target.RealityShortId = source.RealityShortId;
        target.Fingerprint = source.Fingerprint;
        target.RealitySpiderX = source.RealitySpiderX;
        target.Sni = source.Sni;
        target.Remark = source.Remark;
        if (!string.IsNullOrWhiteSpace(source.Region) && source.Region != "-")
        {
            target.Region = source.Region;
        }

        target.SubscriptionName = source.SubscriptionName;
        target.IsCloudManaged = source.IsCloudManaged;
        target.IsLocalManual = source.IsLocalManual;
        target.IsLocalSubscription = source.IsLocalSubscription;
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

    private static string SanitizeFileName(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new string(value.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "Nexora-Node" : sanitized;
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

    private static void ShowError(Exception exception)
    {
        DiagnosticLogService.Error(exception.Message, exception);
        MessageBox.Show(exception.Message, "Nexora", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
