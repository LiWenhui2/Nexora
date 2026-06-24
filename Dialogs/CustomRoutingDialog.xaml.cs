using System.Windows;
using System.Windows.Media;
using NaiwaProxy.Models;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace NaiwaProxy.Dialogs;

public partial class CustomRoutingDialog : Window
{
    private static readonly SolidColorBrush PlaceholderBrush = new(System.Windows.Media.Color.FromRgb(0x94, 0xA3, 0xB8));
    private static readonly SolidColorBrush TextBrush = new(System.Windows.Media.Color.FromRgb(0x0F, 0x17, 0x2A));

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

        ConfigurePlaceholder(ProxyDomainsBox, "google.com\r\ngeosite:google\r\ndomain:example.com");
        ConfigurePlaceholder(DirectDomainsBox, "baidu.com\r\ngeosite:cn\r\nfull:www.example.cn");
        ConfigurePlaceholder(BlockDomainsBox, "ads.example.com\r\nregexp:.*\\.ad\\..*$");
        ConfigurePlaceholder(ProxyIpsBox, "8.8.8.8\r\n1.1.1.1/32");
        ConfigurePlaceholder(DirectIpsBox, "192.168.0.0/16\r\ngeoip:cn\r\ngeoip:private");
        ConfigurePlaceholder(BlockIpsBox, "0.0.0.0/32\r\n127.0.0.1");
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        Routing = new CustomRoutingSettings
        {
            ProxyDomains = FromText(ProxyDomainsBox),
            DirectDomains = FromText(DirectDomainsBox),
            BlockDomains = FromText(BlockDomainsBox),
            ProxyIps = FromText(ProxyIpsBox),
            DirectIps = FromText(DirectIpsBox),
            BlockIps = FromText(BlockIpsBox)
        };
        DialogResult = true;
        Close();
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
            .Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.StartsWith("#", StringComparison.Ordinal))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
