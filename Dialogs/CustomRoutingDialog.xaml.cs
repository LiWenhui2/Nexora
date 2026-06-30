using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NaiwaProxy.Models;
using Drawing = System.Drawing;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace NaiwaProxy.Dialogs;

public partial class CustomRoutingDialog : Window
{
    private static readonly SolidColorBrush PlaceholderBrush = new(System.Windows.Media.Color.FromRgb(0x94, 0xA3, 0xB8));
    private static readonly SolidColorBrush TextBrush = new(System.Windows.Media.Color.FromRgb(0x0F, 0x17, 0x2A));
    private readonly ObservableCollection<DesktopAppOption> _desktopApps = [];
    private readonly ICollectionView _desktopAppsView;
    private readonly string _routingMode;

    public CustomRoutingSettings Routing { get; private set; }

    public CustomRoutingDialog(CustomRoutingSettings routing, string routingMode)
    {
        InitializeComponent();
        Routing = Clone(routing);
        _routingMode = string.IsNullOrWhiteSpace(routingMode) ? "BypassChina" : routingMode;

        ProxyDomainsBox.Text = ToText(Routing.ProxyDomains);
        DirectDomainsBox.Text = ToText(Routing.DirectDomains);
        BypassChinaDomainsBox.Text = ToText(Routing.BypassChinaDomains);
        BlockDomainsBox.Text = ToText(Routing.BlockDomains);
        ProxyIpsBox.Text = ToText(Routing.ProxyIps);
        DirectIpsBox.Text = ToText(Routing.DirectIps);
        BypassChinaIpsBox.Text = ToText(Routing.BypassChinaIps);
        BlockIpsBox.Text = ToText(Routing.BlockIps);
        ProxyProcessesBox.Text = ToText(Routing.ProxyProcesses);
        DirectProcessesBox.Text = ToText(Routing.DirectProcesses);
        BypassChinaProcessesBox.Text = ToText(Routing.BypassChinaProcesses);
        BlockProcessesBox.Text = ToText(Routing.BlockProcesses);

        ConfigurePlaceholder(ProxyDomainsBox, "google.com\r\ngeosite:google\r\ndomain:example.com");
        ConfigurePlaceholder(DirectDomainsBox, "baidu.com\r\ngeosite:cn\r\nfull:www.example.cn");
        ConfigurePlaceholder(BypassChinaDomainsBox, "example.cn\r\ngeosite:cn");
        ConfigurePlaceholder(BlockDomainsBox, "ads.example.com\r\nregexp:.*\\.ad\\..*$");
        ConfigurePlaceholder(ProxyIpsBox, "8.8.8.8\r\n1.1.1.1/32");
        ConfigurePlaceholder(DirectIpsBox, "192.168.0.0/16\r\ngeoip:private");
        ConfigurePlaceholder(BypassChinaIpsBox, "geoip:cn\r\n223.5.5.5");
        ConfigurePlaceholder(BlockIpsBox, "0.0.0.0/32\r\n127.0.0.1");
        ConfigurePlaceholder(ProxyProcessesBox, "chrome.exe");
        ConfigurePlaceholder(DirectProcessesBox, "WeChat.exe");
        ConfigurePlaceholder(BypassChinaProcessesBox, "Telegram.exe");
        ConfigurePlaceholder(BlockProcessesBox, "example.exe");

        _desktopAppsView = CollectionViewSource.GetDefaultView(_desktopApps);
        _desktopAppsView.Filter = FilterDesktopApp;
        AppListBox.ItemsSource = _desktopAppsView;
        ConfigureRouteVisibility();
        LoadDesktopApps();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        Routing = new CustomRoutingSettings
        {
            ProxyDomains = FromText(ProxyDomainsBox),
            DirectDomains = FromText(DirectDomainsBox),
            BypassChinaDomains = FromText(BypassChinaDomainsBox),
            BlockDomains = FromText(BlockDomainsBox),
            ProxyIps = FromText(ProxyIpsBox),
            DirectIps = FromText(DirectIpsBox),
            BypassChinaIps = FromText(BypassChinaIpsBox),
            BlockIps = FromText(BlockIpsBox),
            ProxyProcesses = FromText(ProxyProcessesBox).Select(NormalizeProcessName).Where(IsNotBlank).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            DirectProcesses = FromText(DirectProcessesBox).Select(NormalizeProcessName).Where(IsNotBlank).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            BypassChinaProcesses = FromText(BypassChinaProcessesBox).Select(NormalizeProcessName).Where(IsNotBlank).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            BlockProcesses = FromText(BlockProcessesBox).Select(NormalizeProcessName).Where(IsNotBlank).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        };
        DialogResult = true;
        Close();
    }

    private void AppSearchBox_TextChanged(object sender, TextChangedEventArgs e) => _desktopAppsView.Refresh();

    private void RefreshAppsButton_Click(object sender, RoutedEventArgs e) => LoadDesktopApps();

    private void AddSelectedAppsToProxy_Click(object sender, RoutedEventArgs e) => AddSelectedAppsTo(ProxyProcessesBox);

    private void AddSelectedAppsToDirect_Click(object sender, RoutedEventArgs e) => AddSelectedAppsTo(DirectProcessesBox);

    private void AddSelectedAppsToBypassChina_Click(object sender, RoutedEventArgs e) => AddSelectedAppsTo(BypassChinaProcessesBox);

    private void AddSelectedAppsToBlock_Click(object sender, RoutedEventArgs e) => AddSelectedAppsTo(BlockProcessesBox);

    private void AddSelectedAppsTo(WpfTextBox target)
    {
        var processNames = AppListBox.SelectedItems
            .OfType<DesktopAppOption>()
            .Select(app => app.ProcessName)
            .Where(IsNotBlank)
            .ToList();

        if (processNames.Count == 0)
        {
            ShowRuleHint("请先选择一个或多个桌面软件。");
            return;
        }

        var existing = FromText(target);
        existing.AddRange(processNames);
        var normalized = existing
            .Select(NormalizeProcessName)
            .Where(IsNotBlank)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        target.Foreground = TextBrush;
        target.Text = ToText(normalized);
        ShowRuleHint($"已添加 {processNames.Distinct(StringComparer.OrdinalIgnoreCase).Count()} 个桌面软件规则。");
    }

    private void AppListBox_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var item = FindAncestor<ListBoxItem>((DependencyObject)e.OriginalSource);
        if (item is null)
        {
            return;
        }

        if (!item.IsSelected)
        {
            AppListBox.SelectedItems.Clear();
            item.IsSelected = true;
        }

        item.Focus();
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        var current = source;
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private void ShowRuleHint(string message)
    {
        RuleHintText.Text = message;
    }

    private void LoadDesktopApps()
    {
        var apps = Process.GetProcesses()
            .Select(TryCreateDesktopAppOption)
            .Where(app => app is not null)
            .Select(app => app!)
            .GroupBy(app => app.ProcessName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(app => app.Title.Length).First())
            .OrderBy(app => app.Title, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(app => app.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _desktopApps.Clear();
        foreach (var app in apps)
        {
            _desktopApps.Add(app);
        }
    }

    private static DesktopAppOption? TryCreateDesktopAppOption(Process process)
    {
        try
        {
            if (process.Id == Environment.ProcessId || string.IsNullOrWhiteSpace(process.MainWindowTitle))
            {
                return null;
            }

            var processName = NormalizeProcessName(process.ProcessName);
            if (string.IsNullOrWhiteSpace(processName))
            {
                return null;
            }

            return new DesktopAppOption(process.MainWindowTitle.Trim(), processName, TryGetProcessIcon(process));
        }
        catch
        {
            return null;
        }
        finally
        {
            process.Dispose();
        }
    }

    private static ImageSource? TryGetProcessIcon(Process process)
    {
        try
        {
            var fileName = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(fileName) || !File.Exists(fileName))
            {
                return null;
            }

            using var icon = Drawing.Icon.ExtractAssociatedIcon(fileName);
            if (icon is null)
            {
                return null;
            }

            var source = Imaging.CreateBitmapSourceFromHIcon(
                icon.Handle,
                Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(18, 18));
            source.Freeze();
            return source;
        }
        catch
        {
            return null;
        }
    }

    private bool FilterDesktopApp(object item)
    {
        if (item is not DesktopAppOption app)
        {
            return false;
        }

        var keyword = AppSearchBox.Text?.Trim();
        return string.IsNullOrWhiteSpace(keyword) ||
               app.Title.Contains(keyword, StringComparison.CurrentCultureIgnoreCase) ||
               app.ProcessName.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }

    private void ConfigureRouteVisibility()
    {
        HideDefaultRouteAction(_routingMode);
    }

    private void HideDefaultRouteAction(string routingMode)
    {
        switch (routingMode)
        {
            case "Global":
                ProxyRulesPanel.Visibility = Visibility.Collapsed;
                AddAppsToProxyMenuItem.Visibility = Visibility.Collapsed;
                break;
            case "BypassChina":
                BypassChinaRulesPanel.Visibility = Visibility.Collapsed;
                AddAppsToBypassChinaMenuItem.Visibility = Visibility.Collapsed;
                break;
            case "Direct":
                DirectRulesPanel.Visibility = Visibility.Collapsed;
                AddAppsToDirectMenuItem.Visibility = Visibility.Collapsed;
                break;
        }
    }

    private static void ConfigurePlaceholder(WpfTextBox textBox, string placeholder)
    {
        textBox.Tag = placeholder;
        if (string.IsNullOrWhiteSpace(textBox.Text))
        {
            ApplyPlaceholder(textBox);
        }

        textBox.GotFocus += (_, _) =>
        {
            if (IsShowingPlaceholder(textBox))
            {
                textBox.Text = "";
                textBox.Foreground = TextBrush;
            }
        };

        textBox.LostFocus += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(textBox.Text))
            {
                ApplyPlaceholder(textBox);
            }
        };
    }

    private static void ApplyPlaceholder(WpfTextBox textBox)
    {
        textBox.Text = (string)textBox.Tag;
        textBox.Foreground = PlaceholderBrush;
    }

    private static bool IsShowingPlaceholder(WpfTextBox textBox) =>
        textBox.Tag is string placeholder && textBox.Text == placeholder;

    private static List<string> FromText(WpfTextBox textBox)
    {
        if (IsShowingPlaceholder(textBox))
        {
            return [];
        }

        return FromText(textBox.Text);
    }

    private static CustomRoutingSettings Clone(CustomRoutingSettings routing)
    {
        return new CustomRoutingSettings
        {
            ProxyDomains = [.. routing.ProxyDomains],
            DirectDomains = [.. routing.DirectDomains],
            BypassChinaDomains = [.. routing.BypassChinaDomains],
            BlockDomains = [.. routing.BlockDomains],
            ProxyIps = [.. routing.ProxyIps],
            DirectIps = [.. routing.DirectIps],
            BypassChinaIps = [.. routing.BypassChinaIps],
            BlockIps = [.. routing.BlockIps],
            ProxyProcesses = [.. routing.ProxyProcesses],
            DirectProcesses = [.. routing.DirectProcesses],
            BypassChinaProcesses = [.. routing.BypassChinaProcesses],
            BlockProcesses = [.. routing.BlockProcesses]
        };
    }

    private static string ToText(IEnumerable<string> values) => string.Join(Environment.NewLine, values);

    private static List<string> FromText(string text)
    {
        return text
            .Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.StartsWith("#", StringComparison.Ordinal))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string NormalizeProcessName(string value)
    {
        var trimmed = value.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return "";
        }

        var fileName = Path.GetFileName(trimmed);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return "";
        }

        return fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? fileName : $"{fileName}.exe";
    }

    private static bool IsNotBlank(string value) => !string.IsNullOrWhiteSpace(value);

    private sealed record DesktopAppOption(string Title, string ProcessName, ImageSource? Icon);
}
