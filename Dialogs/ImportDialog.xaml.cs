using System.Windows;
using NaiwaProxy.Models;
using NaiwaProxy.Services;

namespace NaiwaProxy.Dialogs;

public partial class ImportDialog : Window
{
    public List<VmessProfile> ImportedProfiles { get; } = [];

    public ImportDialog()
    {
        InitializeComponent();
    }

    private async void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ImportButton.IsEnabled = false;
            ImportButton.Content = "导入中…";
            ImportedProfiles.Clear();
            ImportedProfiles.AddRange(await SubscriptionImportService.ImportAsync(ImportBox.Text));

            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "NaiwaProxy", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            ImportButton.IsEnabled = true;
            ImportButton.Content = "导入";
        }
    }
}
