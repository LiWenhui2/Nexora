using System.Windows;
using NaiwaProxy.Services;
using Brush = System.Windows.Media.Brush;

namespace NaiwaProxy.Dialogs;

public enum ThemedMessageKind
{
    Information,
    Warning,
    Error
}

public partial class ThemedMessageDialog : Window
{
    private ThemedMessageDialog(
        Window? owner,
        string headline,
        IReadOnlyList<string>? details,
        ThemedMessageKind kind,
        string? title)
    {
        Owner = owner;
        InitializeComponent();
        DialogThemeService.Apply(this, DialogThemeService.ResolveAccent(owner));

        if (!string.IsNullOrWhiteSpace(title))
        {
            Title = title;
        }

        HeadlineText.Text = headline;
        ApplyKind(kind);

        if (details is { Count: > 0 })
        {
            DetailsItems.ItemsSource = details;
            DetailsItems.Visibility = Visibility.Visible;
        }
    }

    public static void Show(
        Window? owner,
        string headline,
        IEnumerable<string>? details = null,
        ThemedMessageKind kind = ThemedMessageKind.Information,
        string title = "Nexora")
    {
        var detailLines = details?
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();

        var dialog = new ThemedMessageDialog(owner, headline, detailLines, kind, title);
        dialog.ShowDialog();
    }

    private void ApplyKind(ThemedMessageKind kind)
    {
        switch (kind)
        {
            case ThemedMessageKind.Warning:
                IconText.Text = "!";
                IconBorder.Background = (Brush)FindResource("WarningIconBackgroundBrush");
                IconBorder.BorderBrush = (Brush)FindResource("WarningIconBorderBrush");
                IconText.Foreground = (Brush)FindResource("WarningIconBrush");
                break;
            case ThemedMessageKind.Error:
                IconText.Text = "×";
                IconBorder.Background = (Brush)FindResource("ErrorIconBackgroundBrush");
                IconBorder.BorderBrush = (Brush)FindResource("ErrorIconBorderBrush");
                IconText.Foreground = (Brush)FindResource("ErrorIconBrush");
                break;
            default:
                IconText.Text = "i";
                IconBorder.Background = (Brush)FindResource("InfoIconBackgroundBrush");
                IconBorder.BorderBrush = (Brush)FindResource("InfoIconBorderBrush");
                IconText.Foreground = (Brush)FindResource("InfoIconBrush");
                break;
        }
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
