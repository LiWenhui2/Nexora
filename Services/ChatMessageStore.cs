using System.IO;
using System.Text.Json;
using NaiwaProxy.Models;

namespace NaiwaProxy.Services;

public sealed class ChatMessageStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _directory;

    public ChatMessageStore()
    {
        _directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Nexora",
            "chat-messages");
        Directory.CreateDirectory(_directory);
    }

    public List<ChatMessage> Load(int userId)
    {
        if (userId <= 0)
        {
            return [];
        }

        var path = GetPath(userId);
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<List<ChatMessage>>(json, JsonOptions) ?? [];
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Warning($"Failed to load chat messages for user {userId}: {ex.Message}");
            return [];
        }
    }

    public void Save(int userId, IReadOnlyList<ChatMessage> messages)
    {
        if (userId <= 0)
        {
            return;
        }

        try
        {
            var ordered = messages.OrderBy(m => m.CreatedAt).ToList();
            var json = JsonSerializer.Serialize(ordered, JsonOptions);
            File.WriteAllText(GetPath(userId), json);
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Warning($"Failed to save chat messages for user {userId}: {ex.Message}");
        }
    }

    private string GetPath(int userId) => Path.Combine(_directory, $"{userId}.json");
}
