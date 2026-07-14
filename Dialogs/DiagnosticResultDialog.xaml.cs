using System.Windows;
using NaiwaProxy.Models;
using NaiwaProxy.Services;
using Brush = System.Windows.Media.Brush;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace NaiwaProxy.Dialogs;

public partial class DiagnosticResultDialog : Window
{
    private readonly DiagnosticResult _result;
    private readonly AppSettings _settings;
    private readonly VmessProfile? _activeProfile;

    public DiagnosticResultDialog(
        Window owner,
        DiagnosticResult result,
        AppSettings settings,
        VmessProfile? activeProfile)
    {
        Owner = owner;
        _result = result;
        _settings = settings;
        _activeProfile = activeProfile;
        InitializeComponent();
        DialogThemeService.Apply(this, DialogThemeService.ResolveAccent(owner));

        CompletedAtText.Text = $"完成时间：{result.CompletedAt:yyyy-MM-dd HH:mm:ss}";
        SummaryText.Text = result.Summary;
        var statusBrush = (Brush)FindResource(result.Success ? "InfoIconBrush" : "ErrorIconBrush");
        var statusBackground = (Brush)FindResource(result.Success ? "InfoIconBackgroundBrush" : "ErrorIconBackgroundBrush");
        var statusBorder = (Brush)FindResource(result.Success ? "InfoIconBorderBrush" : "ErrorIconBorderBrush");
        SummaryText.Foreground = statusBrush;
        SummaryBadge.Background = statusBackground;
        SummaryBadge.BorderBrush = statusBorder;
        SummaryBadge.BorderThickness = new Thickness(1);

        ChecksItems.ItemsSource = result.Checks.Select(check => new DiagnosticCheckView(
            check.Success ? "✓" : "×",
            check.Name,
            check.Detail,
            (Brush)FindResource(check.Success ? "InfoIconBrush" : "ErrorIconBrush"))).ToList();
    }

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        var saveDialog = new SaveFileDialog
        {
            Title = "导出 Nexora 脱敏诊断包",
            Filter = "ZIP 诊断包 (*.zip)|*.zip",
            FileName = $"Nexora-Diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
            AddExtension = true,
            DefaultExt = ".zip"
        };
        if (saveDialog.ShowDialog(this) != true)
        {
            return;
        }

        ExportButton.IsEnabled = false;
        ExportButton.Content = "正在导出...";
        try
        {
            await Task.Run(() => DiagnosticReportService.ExportSupportBundle(
                _result,
                _settings,
                _activeProfile,
                saveDialog.FileName));
            ThemedMessageDialog.Show(this, $"诊断包已导出：{saveDialog.FileName}");
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Error("Diagnostic bundle export failed.", ex);
            ThemedMessageDialog.Show(
                this,
                "诊断包导出失败",
                [ex.Message],
                ThemedMessageKind.Error);
        }
        finally
        {
            ExportButton.IsEnabled = true;
            ExportButton.Content = "导出诊断包";
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private sealed record DiagnosticCheckView(string Symbol, string Name, string Detail, Brush StatusBrush);
}
