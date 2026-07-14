using System.IO;
using System.IO.Compression;
using System.Text.Json;
using NaiwaProxy.Models;

namespace NaiwaProxy.Services;

public static class ConfigurationBackupService
{
    private const int MaximumAutomaticBackups = 5;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Nexora");
    public static string BackupDirectory => Path.Combine(DataDirectory, "backups");

    public static string Create(AppSettings settings, string reason, string? destinationPath = null)
    {
        Directory.CreateDirectory(BackupDirectory);
        var safeReason = new string(reason.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_').ToArray());
        if (string.IsNullOrWhiteSpace(safeReason)) safeReason = "manual";
        var path = destinationPath ?? Path.Combine(
            BackupDirectory,
            $"Nexora-{safeReason}-{DateTime.Now:yyyyMMdd-HHmmssfff}.nexora-backup");
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        if (File.Exists(path)) File.Delete(path);

        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteEntry(archive, "settings.json", JsonSerializer.Serialize(settings, JsonOptions));
        WriteEntry(archive, "manifest.json", JsonSerializer.Serialize(new BackupManifest
        {
            FormatVersion = 1,
            ConfigurationVersion = settings.ConfigurationVersion,
            AppVersion = AppVersionHelper.GetCurrentVersionName(),
            CreatedAt = DateTime.Now,
            Reason = reason,
            ProfileCount = settings.Profiles.Count,
            SubscriptionCount = settings.SubscriptionSources.Count
        }, JsonOptions));

        if (destinationPath is null) PruneAutomaticBackups();
        DiagnosticLogService.Info($"Configuration backup created: {path}");
        return path;
    }

    public static AppSettings Restore(string backupPath)
    {
        using var archive = ZipFile.OpenRead(backupPath);
        var entry = archive.GetEntry("settings.json")
            ?? throw new InvalidDataException("备份中缺少 settings.json。");
        using var stream = entry.Open();
        var settings = JsonSerializer.Deserialize<AppSettings>(stream, JsonOptions)
            ?? throw new InvalidDataException("备份配置为空。");
        settings.ConfigurationVersion = Math.Max(1, settings.ConfigurationVersion);
        return settings;
    }

    public static AppSettings? TryRestoreLatest()
    {
        foreach (var path in ListBackups())
        {
            try
            {
                var settings = Restore(path);
                DiagnosticLogService.Warning($"Recovered corrupted settings from backup: {path}");
                return settings;
            }
            catch (Exception ex)
            {
                DiagnosticLogService.Warning($"Skipped invalid backup {path}: {ex.Message}");
            }
        }

        return null;
    }

    public static IReadOnlyList<string> ListBackups()
    {
        if (!Directory.Exists(BackupDirectory)) return [];
        return Directory.EnumerateFiles(BackupDirectory, "*.nexora-backup")
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Select(file => file.FullName)
            .ToList();
    }

    public static void EnsureDailyBackup(AppSettings settings)
    {
        var latest = ListBackups().FirstOrDefault();
        if (latest is null || DateTime.Now - File.GetLastWriteTime(latest) >= TimeSpan.FromHours(24))
        {
            Create(settings, latest is null ? "initial" : "daily");
        }
    }

    private static void PruneAutomaticBackups()
    {
        foreach (var path in ListBackups().Skip(MaximumAutomaticBackups))
        {
            try { File.Delete(path); }
            catch (Exception ex) { DiagnosticLogService.Warning($"Failed to prune backup {path}: {ex.Message}"); }
        }
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Fastest);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }

    private sealed class BackupManifest
    {
        public int FormatVersion { get; set; }
        public int ConfigurationVersion { get; set; }
        public string AppVersion { get; set; } = "";
        public DateTime CreatedAt { get; set; }
        public string Reason { get; set; } = "";
        public int ProfileCount { get; set; }
        public int SubscriptionCount { get; set; }
    }
}
