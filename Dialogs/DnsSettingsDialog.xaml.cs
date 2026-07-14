using System.Windows;
using System.Windows.Controls;
using NaiwaProxy.Models;
using NaiwaProxy.Services;

namespace NaiwaProxy.Dialogs;

public partial class DnsSettingsDialog : Window
{
    private readonly AppSettings _settings;
    private readonly SettingsStore _settingsStore;

    public DnsSettingsDialog(Window owner, AppSettings settings, SettingsStore settingsStore)
    {
        Owner = owner;
        _settings = settings;
        _settingsStore = settingsStore;
        InitializeComponent();
        DialogThemeService.Apply(this, DialogThemeService.ResolveAccent(owner));
        IpPreferenceCombo.SelectedItem = IpPreferenceCombo.Items.OfType<ComboBoxItem>().FirstOrDefault(item => string.Equals(item.Tag as string, settings.IpPreferenceMode, StringComparison.OrdinalIgnoreCase)) ?? IpPreferenceCombo.Items[2];
        DomesticDnsTextBox.Text = settings.DomesticDnsServer;
        ProxyDnsTextBox.Text = settings.ProxyDnsServer;
        DnsOverHttpsToggle.IsChecked = settings.DnsOverHttpsEnabled;
        Ipv6FallbackToggle.IsChecked = settings.Ipv6AutoFallbackEnabled;
    }

    private bool TryApplyInputs(AppSettings target)
    {
        var domestic = DomesticDnsTextBox.Text.Trim();
        var proxy = ProxyDnsTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(domestic) || string.IsNullOrWhiteSpace(proxy))
        {
            ThemedMessageDialog.Show(this, "DNS 地址不能为空", ["请填写国内 DNS 和代理 DNS。"], ThemedMessageKind.Warning);
            return false;
        }
        target.IpPreferenceMode = (IpPreferenceCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "PreferIPv4";
        target.DomesticDnsServer = domestic;
        target.ProxyDnsServer = proxy;
        target.DnsOverHttpsEnabled = DnsOverHttpsToggle.IsChecked == true;
        target.Ipv6AutoFallbackEnabled = Ipv6FallbackToggle.IsChecked == true;
        return true;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryApplyInputs(_settings)) return;
        _settingsStore.Save(_settings);
        DialogResult = true;
    }

    private async void DnsTestButton_Click(object sender, RoutedEventArgs e)
    {
        var testSettings = new AppSettings { IsTunEnabled = _settings.IsTunEnabled };
        if (!TryApplyInputs(testSettings)) return;
        DnsTestButton.IsEnabled = false;
        DnsTestButton.Content = "正在检查...";
        try
        {
            var result = await DnsDiagnosticsService.RunAsync(testSettings);
            ThemedMessageDialog.Show(this, result.Success ? "DNS 与 IPv6 状态正常" : "DNS 配置存在风险", result.Details,
                result.Success ? ThemedMessageKind.Information : ThemedMessageKind.Warning);
        }
        catch (Exception ex)
        {
            ThemedMessageDialog.Show(this, "DNS 检查失败", [ex.Message], ThemedMessageKind.Error);
        }
        finally
        {
            DnsTestButton.IsEnabled = true;
            DnsTestButton.Content = "DNS 泄漏测试";
        }
    }
}
