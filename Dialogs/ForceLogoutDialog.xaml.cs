using System.Windows;
using NaiwaProxy.Services;

namespace NaiwaProxy.Dialogs;

public partial class ForceLogoutDialog : Window
{
    private ForceLogoutDialog(Window? owner, ForceLogoutPushMessage payload)
    {
        Owner = owner;
        InitializeComponent();
        DialogThemeService.Apply(this, DialogThemeService.ResolveAccent(owner));

        HeadlineText.Text = payload.GetHeadline();
        DescriptionText.Text = payload.GetDisplayMessage();
        ConfirmButton.Content = payload.GetConfirmButtonText();
        var details = payload.BuildUserFriendlyDetails();
        if (details.Count > 0)
        {
            DetailsItems.ItemsSource = details;
            DetailsPanel.Visibility = Visibility.Visible;
        }
    }

    public static void Show(Window? owner, ForceLogoutPushMessage payload)
    {
        var dialog = new ForceLogoutDialog(owner, payload);
        dialog.ShowDialog();
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
