using System.Windows;
using NaiwaProxy.Models;

namespace NaiwaProxy.Dialogs;

public partial class RequiredVersionUpdateDialog : Window
{
    public event Action? ActionRequested;

    public RequiredVersionUpdateDialog(AppUpdateRelease release, string currentVersion)
    {
        InitializeComponent();
        VersionLineText.Text = $"v{currentVersion} → v{release.VersionName}";
        LeadText.Text = $"为保障正常使用，请先更新到 v{release.VersionName}。更新完成后即可继续使用 Nexora。";
        ReleaseTitleText.Text = string.IsNullOrWhiteSpace(release.Title) ? "版本更新说明" : release.Title;
        ReleaseContentText.Text = string.IsNullOrWhiteSpace(release.Content)
            ? "本次更新包含重要改进，建议尽快完成安装。"
            : release.Content;
        Closing += RequiredVersionUpdateDialog_Closing;
    }

    public void SetDownloading()
    {
        ActionButton.IsEnabled = false;
        ActionButton.Content = "正在准备更新...";
        ProgressPanel.Visibility = Visibility.Visible;
        StatusText.Visibility = Visibility.Collapsed;
        ProgressBar.IsIndeterminate = true;
        ProgressBar.Value = 0;
        ProgressText.Text = "正在下载安装包，请稍候...";
    }

    public void UpdateProgress(double percentage, string message)
    {
        ProgressPanel.Visibility = Visibility.Visible;
        StatusText.Visibility = Visibility.Collapsed;
        ProgressBar.IsIndeterminate = false;
        ProgressBar.Value = percentage;
        ProgressText.Text = message;
    }

    public void SetReadyToInstall()
    {
        ProgressPanel.Visibility = Visibility.Collapsed;
        StatusText.Visibility = Visibility.Visible;
        StatusText.Text = "安装包已准备就绪，请点击下方按钮完成安装。";
        ActionButton.IsEnabled = true;
        ActionButton.Content = "立即安装";
    }

    public void SetDownloadFailed(string message)
    {
        ProgressPanel.Visibility = Visibility.Collapsed;
        StatusText.Visibility = Visibility.Visible;
        StatusText.Text = message;
        ActionButton.IsEnabled = true;
        ActionButton.Content = "重新下载";
    }

    public void SetInstallReadyWithoutDownload()
    {
        SetReadyToInstall();
    }

    private void ActionButton_Click(object sender, RoutedEventArgs e)
    {
        ActionRequested?.Invoke();
    }

    private void RequiredVersionUpdateDialog_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        e.Cancel = true;
    }
}
