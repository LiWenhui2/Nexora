using System.Windows;
using System.IO;
using Microsoft.Win32;
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

    private void PasteButton_Click(object sender, RoutedEventArgs e)
    {
        if (Clipboard.ContainsText())
        {
            ImportBox.Text = Clipboard.GetText();
        }
    }

    private async void OpenFileButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择节点文本文件",
            Filter = "文本和配置文件|*.txt;*.conf;*.json;*.yaml;*.yml|所有文件|*.*"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        ImportBox.Text = await File.ReadAllTextAsync(dialog.FileName);
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
