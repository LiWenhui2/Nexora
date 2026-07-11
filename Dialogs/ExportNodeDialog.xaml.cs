using System.Windows;
using NaiwaProxy.Models;
using NaiwaProxy.Services;
using Clipboard = System.Windows.Clipboard;

namespace NaiwaProxy.Dialogs;

public partial class ExportNodeDialog : Window
{
    public ExportNodeDialog(VmessProfile profile, Window? owner = null)
    {
        InitializeComponent();
        if (owner is not null)
        {
            Owner = owner;
        }

        DialogThemeService.Apply(this, DialogThemeService.ResolveAccent(owner));

        NodeNameText.Text = profile.DisplayName;
        var link = ShareLinkBuilder.Build(profile);
        ShareLinkBox.Text = link;
        QrImage.Source = QrCodeService.Generate(link);
    }

    private void CopyLinkButton_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(ShareLinkBox.Text))
        {
            Clipboard.SetText(ShareLinkBox.Text);
        }
    }
}
