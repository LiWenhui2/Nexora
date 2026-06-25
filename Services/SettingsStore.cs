using System.IO;
using System.Text.Json;
using NaiwaProxy.Models;

namespace NaiwaProxy.Services;

public sealed class SettingsStore
{
    private readonly string _settingsDirectory;
    private readonly string _settingsPath;

    public SettingsStore()
    {
        _settingsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Nexora");
        Directory.CreateDirectory(_settingsDirectory);
        _settingsPath = Path.Combine(_settingsDirectory, "settings.json");
    }

    public AppSettings Load()
    {
        if (!File.Exists(_settingsPath))
        {
            return new AppSettings();
        }

        try
        {
            var json = File.ReadAllText(_settingsPath);
            if (IsCorruptedContent(json))
            {
                throw new JsonException("Settings file is empty or starts with an invalid value.");
            }

            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions()) ?? new AppSettings();
            var migrated = MigrateSettings(settings);
            if (!string.Equals(settings.AuthApiBaseUrl, migrated.AuthApiBaseUrl, StringComparison.Ordinal))
            {
                Save(migrated);
            }

            return migrated;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            var backupPath = BackupCorruptedSettings();
            DiagnosticLogService.Warning(
                $"Settings file could not be loaded and was reset. Backup: {backupPath ?? "unavailable"}");
            DiagnosticLogService.Error("Failed to load settings.", ex);

            var settings = new AppSettings();
            Save(settings);
            return settings;
        }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(_settingsDirectory);
        var json = JsonSerializer.Serialize(settings, JsonOptions());
        var tempPath = $"{_settingsPath}.tmp";

        File.WriteAllText(tempPath, json);
        File.Move(tempPath, _settingsPath, overwrite: true);
    }

    private static bool IsCorruptedContent(string json)
    {
        return string.IsNullOrWhiteSpace(json) || json.TrimStart().StartsWith('\0');
    }

    private string? BackupCorruptedSettings()
    {
        if (!File.Exists(_settingsPath))
        {
            return null;
        }

        var backupPath = Path.Combine(
            _settingsDirectory,
            $"settings.corrupt-{DateTime.Now:yyyyMMddHHmmss}.json");

        try
        {
            File.Copy(_settingsPath, backupPath, overwrite: true);
            return backupPath;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLogService.Warning($"Failed to backup corrupted settings: {ex.Message}");
            return null;
        }
    }

    private static AppSettings MigrateSettings(AppSettings settings)
    {
        var normalizedApi = ApiDefaults.NormalizeAuthApiBaseUrl(settings.AuthApiBaseUrl);
        if (!string.Equals(settings.AuthApiBaseUrl, normalizedApi, StringComparison.Ordinal))
        {
            settings.AuthApiBaseUrl = normalizedApi;
        }

        if (string.IsNullOrWhiteSpace(settings.ThemeAccentColor))
        {
            settings.ThemeAccentColor = ThemeService.DefaultAccentHex;
        }

        return settings;
    }

    private static JsonSerializerOptions JsonOptions()
    {
        return new JsonSerializerOptions
        {
            WriteIndented = true
        };
    }
}
