using System.IO;
using NaiwaProxy.Models;

namespace NaiwaProxy.Services;

public static class ChatMessageHelper
{
    private static readonly HashSet<string> BlockedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".bat", ".cmd", ".sh", ".msi", ".dll", ".jar"
    };

    public static bool IsBlockedExtension(string filePath)
    {
        var extension = Path.GetExtension(filePath);
        return !string.IsNullOrWhiteSpace(extension) && BlockedExtensions.Contains(extension);
    }

    public static bool IsImageAttachment(ChatMessage message) =>
        string.Equals(message.AttachmentType, "IMAGE", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(message.MessageType, "IMAGE", StringComparison.OrdinalIgnoreCase);

    public static bool IsFileAttachment(ChatMessage message) =>
        string.Equals(message.AttachmentType, "FILE", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(message.MessageType, "FILE", StringComparison.OrdinalIgnoreCase);

    public static bool HasAttachment(ChatMessage message) =>
        !string.IsNullOrWhiteSpace(message.FileUrl);

    public static string GetPreviewText(ChatMessage message)
    {
        if (!string.IsNullOrWhiteSpace(message.Content))
        {
            return message.Content.Trim();
        }

        if (IsImageAttachment(message))
        {
            return "[图片]";
        }

        if (HasAttachment(message))
        {
            return string.IsNullOrWhiteSpace(message.FileName) ? "[文件]" : $"[文件] {message.FileName.Trim()}";
        }

        return "暂无内容";
    }

    public static string GetCopyableText(ChatMessage message)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(message.Content))
        {
            parts.Add(message.Content.Trim());
        }

        if (!string.IsNullOrWhiteSpace(message.FileName) && !IsImageAttachment(message))
        {
            parts.Add(message.FileName.Trim());
        }

        return parts.Count == 0 ? string.Empty : string.Join(Environment.NewLine, parts);
    }

    public static string FormatFileSize(long? bytes)
    {
        if (bytes is null or < 0)
        {
            return "未知大小";
        }

        var value = (double)bytes.Value;
        string[] units = ["B", "KB", "MB", "GB"];
        var unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return unitIndex == 0
            ? $"{value:0} {units[unitIndex]}"
            : $"{value:0.##} {units[unitIndex]}";
    }

    public static string FormatFileSizeCompact(long? bytes)
    {
        if (bytes is null or < 0)
        {
            return "未知大小";
        }

        var value = (double)bytes.Value;
        if (value >= 1024 * 1024 * 1024)
        {
            return $"{value / (1024 * 1024 * 1024):0.#}G";
        }

        if (value >= 1024 * 1024)
        {
            return $"{value / (1024 * 1024):0.#}M";
        }

        if (value >= 1024)
        {
            return $"{value / 1024:0.#}K";
        }

        return $"{value:0}B";
    }

    public static string GetFileExtensionLabel(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return "FILE";
        }

        var extension = Path.GetExtension(fileName).TrimStart('.');
        if (string.IsNullOrWhiteSpace(extension))
        {
            return "FILE";
        }

        return extension.Length <= 4
            ? extension.ToUpperInvariant()
            : extension[..4].ToUpperInvariant();
    }

    public static (byte R, byte G, byte B) GetFileTypeColor(string? fileName)
    {
        var extension = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();
        return extension switch
        {
            ".zip" or ".rar" or ".7z" or ".tar" or ".gz" => (245, 158, 11),
            ".pdf" => (239, 68, 68),
            ".doc" or ".docx" => (59, 130, 246),
            ".xls" or ".xlsx" => (34, 197, 94),
            ".ppt" or ".pptx" => (249, 115, 22),
            ".txt" or ".md" or ".log" => (100, 116, 139),
            ".mp4" or ".mov" or ".avi" or ".mkv" => (139, 92, 246),
            ".mp3" or ".wav" or ".flac" => (236, 72, 153),
            _ => (100, 116, 139)
        };
    }

    public static bool IsImageFilePath(string filePath)
    {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        return extension is ".jpg" or ".jpeg" or ".png" or ".webp" or ".gif";
    }

    public static string GetMimeType(string filePath) =>
        Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".pdf" => "application/pdf",
            ".txt" => "text/plain",
            ".zip" => "application/zip",
            ".doc" => "application/msword",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".xls" => "application/vnd.ms-excel",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            _ => "application/octet-stream"
        };
}
