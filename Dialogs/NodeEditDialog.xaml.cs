using System.Windows;
using System.Windows.Controls;
using NaiwaProxy.Models;
using MessageBox = System.Windows.MessageBox;

namespace NaiwaProxy.Dialogs;

public partial class NodeEditDialog : Window
{
    public VmessProfile Profile { get; private set; }

    public NodeEditDialog(VmessProfile? profile = null)
    {
        InitializeComponent();
        Profile = profile is null ? new VmessProfile() : CloneProfile(profile);
        LoadProfile(Profile);
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Profile = ReadProfile();
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "NaiwaProxy", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void LoadProfile(VmessProfile profile)
    {
        NameBox.Text = profile.Name;
        SelectCombo(ProtocolBox, profile.Protocol);
        AddressBox.Text = profile.Address;
        PortBox.Text = profile.Port.ToString();
        UuidBox.Text = profile.UserId;
        PasswordBox.Text = profile.Password;
        AlterIdBox.Text = profile.AlterId.ToString();
        HostBox.Text = profile.Host;
        SniBox.Text = profile.Sni;
        PathBox.Text = profile.Path;
        SelectCombo(SecurityBox, profile.Security);
        SelectCombo(NetworkBox, profile.Network);
        SelectCombo(TlsBox, string.IsNullOrWhiteSpace(profile.Tls) ? "none" : profile.Tls);
    }

    private VmessProfile ReadProfile()
    {
        if (!int.TryParse(PortBox.Text, out var port) || port is <= 0 or > 65535)
        {
            throw new InvalidOperationException("端口必须在 1 到 65535 之间。");
        }

        if (!int.TryParse(AlterIdBox.Text, out var alterId) || alterId < 0)
        {
            throw new InvalidOperationException("Alter ID 必须为非负整数。");
        }

        if (string.IsNullOrWhiteSpace(AddressBox.Text))
        {
            throw new InvalidOperationException("地址不能为空。");
        }

        var protocol = SelectedComboValue(ProtocolBox, "vmess");
        if ((protocol is "vmess" or "vless") && !Guid.TryParse(UuidBox.Text.Trim(), out _))
        {
            throw new InvalidOperationException("UUID 格式无效。");
        }

        if ((protocol is "trojan" or "shadowsocks") && string.IsNullOrWhiteSpace(PasswordBox.Text))
        {
            throw new InvalidOperationException("当前协议需要填写密码。");
        }

        return new VmessProfile
        {
            Id = Profile.Id,
            Protocol = protocol,
            Name = NameBox.Text.Trim(),
            Address = AddressBox.Text.Trim(),
            Port = port,
            UserId = UuidBox.Text.Trim(),
            Password = PasswordBox.Text.Trim(),
            AlterId = alterId,
            Security = SelectedComboValue(SecurityBox, "auto"),
            Network = SelectedComboValue(NetworkBox, "tcp"),
            Tls = SelectedComboValue(TlsBox, "none") == "tls" ? "tls" : "",
            Host = HostBox.Text.Trim(),
            Sni = SniBox.Text.Trim(),
            Path = PathBox.Text.Trim()
        };
    }

    private static VmessProfile CloneProfile(VmessProfile source)
    {
        return new VmessProfile
        {
            Id = source.Id,
            Protocol = source.Protocol,
            Name = source.Name,
            Address = source.Address,
            Port = source.Port,
            UserId = source.UserId,
            Password = source.Password,
            AlterId = source.AlterId,
            Security = source.Security,
            Network = source.Network,
            Type = source.Type,
            Host = source.Host,
            Path = source.Path,
            Tls = source.Tls,
            Sni = source.Sni,
            Remark = source.Remark
        };
    }

    private static void SelectCombo(System.Windows.Controls.ComboBox comboBox, string value)
    {
        foreach (ComboBoxItem item in comboBox.Items)
        {
            if (string.Equals(item.Content?.ToString(), value, StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedItem = item;
                return;
            }
        }

        comboBox.SelectedIndex = 0;
    }

    private static string SelectedComboValue(System.Windows.Controls.ComboBox comboBox, string fallback)
    {
        return (comboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? fallback;
    }
}
