using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using NaiwaProxy.Models;
using NaiwaProxy.Services;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using WpfApplication = System.Windows.Application;
using WpfMessageBox = System.Windows.MessageBox;

namespace NaiwaProxy.Dialogs;

public partial class ConfigurationBackupDialog : Window
{
    private readonly AppSettings _settings;
    private readonly SettingsStore _settingsStore;
    private readonly Action _prepareShutdown;

    public ConfigurationBackupDialog(Window owner, AppSettings settings, SettingsStore settingsStore, Action prepareShutdown)
    {
        Owner = owner;
        _settings = settings;
        _settingsStore = settingsStore;
        _prepareShutdown = prepareShutdown;
        InitializeComponent();
        DialogThemeService.Apply(this, DialogThemeService.ResolveAccent(owner));
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        var backups = ConfigurationBackupService.ListBackups();
        OpenBackupButton.IsEnabled = backups.Count > 0;
        BackupStatusText.Text = backups.Count == 0
            ? "暂无可用备份。创建首个备份后即可打开备份目录。"
            : $"已有 {backups.Count} 个备份，最近备份：{File.GetLastWriteTime(backups[0]):yyyy-MM-dd HH:mm:ss}";
        BackupPathText.Text = $"备份位置：{ConfigurationBackupService.BackupDirectory}";
        BackupPathText.ToolTip = ConfigurationBackupService.BackupDirectory;
    }

    private void OpenBackupButton_Click(object sender, RoutedEventArgs e)
    {
        if (ConfigurationBackupService.ListBackups().Count == 0) return;
        Directory.CreateDirectory(ConfigurationBackupService.BackupDirectory);
        Process.Start(new ProcessStartInfo { FileName = ConfigurationBackupService.BackupDirectory, UseShellExecute = true });
    }

    private void BackupButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "备份 Nexora 配置",
            Filter = "Nexora 配置备份 (*.nexora-backup)|*.nexora-backup",
            FileName = $"Nexora-Backup-{DateTime.Now:yyyyMMdd-HHmmss}.nexora-backup",
            DefaultExt = ".nexora-backup",
            AddExtension = true
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            ConfigurationBackupService.Create(_settings, "manual", dialog.FileName);
            ConfigurationBackupService.Create(_settings, "manual-history");
            RefreshStatus();
            ThemedMessageDialog.Show(this, "配置备份完成", [dialog.FileName]);
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Error("Manual configuration backup failed.", ex);
            ThemedMessageDialog.Show(this, "配置备份失败", [ex.Message], ThemedMessageKind.Error);
        }
    }

    private void RestoreButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "恢复 Nexora 配置", Filter = "Nexora 配置备份 (*.nexora-backup)|*.nexora-backup" };
        if (dialog.ShowDialog(this) != true) return;
        if (WpfMessageBox.Show("恢复后将替换节点、订阅、路由规则和全部设置，并重新启动 Nexora。是否继续？",
                "恢复配置", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            ConfigurationBackupService.Create(_settings, "before-restore");
            _settingsStore.Save(ConfigurationBackupService.Restore(dialog.FileName));
            var executable = Environment.ProcessPath!;
            var escapedExecutable = executable.Replace("'", "''", StringComparison.Ordinal);
            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -WindowStyle Hidden -Command \"Start-Sleep -Milliseconds 1500; Start-Process -FilePath '{escapedExecutable}'\"",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
            _prepareShutdown();
            WpfApplication.Current.Shutdown();
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Error("Configuration restore failed.", ex);
            ThemedMessageDialog.Show(this, "恢复配置失败", [ex.Message], ThemedMessageKind.Error);
        }
    }
}
