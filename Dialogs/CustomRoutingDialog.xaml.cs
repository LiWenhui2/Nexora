using System.Windows;
using NaiwaProxy.Models;

namespace NaiwaProxy.Dialogs;

public partial class CustomRoutingDialog : Window
{
    public CustomRoutingSettings Routing { get; private set; }

    public CustomRoutingDialog(CustomRoutingSettings routing)
    {
        InitializeComponent();
        Routing = Clone(routing);
        ProxyDomainsBox.Text = ToText(Routing.ProxyDomains);
        DirectDomainsBox.Text = ToText(Routing.DirectDomains);
        BlockDomainsBox.Text = ToText(Routing.BlockDomains);
        ProxyIpsBox.Text = ToText(Routing.ProxyIps);
        DirectIpsBox.Text = ToText(Routing.DirectIps);
        BlockIpsBox.Text = ToText(Routing.BlockIps);
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        Routing = new CustomRoutingSettings
        {
            ProxyDomains = FromText(ProxyDomainsBox.Text),
            DirectDomains = FromText(DirectDomainsBox.Text),
            BlockDomains = FromText(BlockDomainsBox.Text),
            ProxyIps = FromText(ProxyIpsBox.Text),
            DirectIps = FromText(DirectIpsBox.Text),
            BlockIps = FromText(BlockIpsBox.Text)
        };
        DialogResult = true;
        Close();
    }

    private static CustomRoutingSettings Clone(CustomRoutingSettings routing)
    {
        return new CustomRoutingSettings
        {
            ProxyDomains = [.. routing.ProxyDomains],
            DirectDomains = [.. routing.DirectDomains],
            BlockDomains = [.. routing.BlockDomains],
            ProxyIps = [.. routing.ProxyIps],
            DirectIps = [.. routing.DirectIps],
            BlockIps = [.. routing.BlockIps]
        };
    }

    private static string ToText(IEnumerable<string> values) => string.Join(Environment.NewLine, values);

    private static List<string> FromText(string text)
    {
        return text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.StartsWith("#", StringComparison.Ordinal))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
