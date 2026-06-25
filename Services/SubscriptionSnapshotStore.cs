using System.IO;
using System.Text.Json;
using NaiwaProxy.Models;

namespace NaiwaProxy.Services;

public sealed class SubscriptionSnapshotStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true
    };

    private readonly string _directory;

    public SubscriptionSnapshotStore()
    {
        _directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Nexora",
            "subscription-snapshots");
        Directory.CreateDirectory(_directory);
    }

    public string Save(SubscriptionSnapshot snapshot, string key)
    {
        var safeKey = SanitizeKey(key);
        var path = Path.Combine(_directory, $"{safeKey}.json");
        var json = JsonSerializer.Serialize(snapshot, JsonOptions);
        File.WriteAllText(path, json);
        return path;
    }

    public SubscriptionSnapshot? Load(string key)
    {
        var path = Path.Combine(_directory, $"{SanitizeKey(key)}.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<SubscriptionSnapshot>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Warning($"Failed to load subscription snapshot: {ex.Message}");
            return null;
        }
    }

    private static string SanitizeKey(string key)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(key.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "default" : sanitized;
    }
}
