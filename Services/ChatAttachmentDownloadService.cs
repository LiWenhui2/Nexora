using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using NaiwaProxy.Models;

namespace NaiwaProxy.Services;

public sealed class ChatAttachmentDownloadService
{
    private static readonly string ChatFilesDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Nexora",
        "chat-files");

    public string GetLocalPath(ChatMessage message)
    {
        var safeName = SanitizeFileName(message.FileName) ?? $"attachment-{message.Id}";
        return Path.Combine(ChatFilesDirectory, $"{message.Id}_{safeName}");
    }

    public async Task<string?> EnsureDownloadedAsync(ChatMessage message, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(message.FileUrl) || ChatMessageHelper.IsImageAttachment(message))
        {
            return null;
        }

        var localPath = GetLocalPath(message);
        if (File.Exists(localPath) && await IsLocalFileValidAsync(localPath, message, cancellationToken))
        {
            return localPath;
        }

        Directory.CreateDirectory(ChatFilesDirectory);
        var tempPath = $"{localPath}.download";

        using var response = await DirectHttpClientFactory.Shared.GetAsync(
            message.FileUrl.Trim(),
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using (var target = File.Create(tempPath))
        {
            await source.CopyToAsync(target, cancellationToken);
            await target.FlushAsync(cancellationToken);
        }

        if (!await IsLocalFileValidAsync(tempPath, message, cancellationToken))
        {
            File.Delete(tempPath);
            throw new InvalidOperationException("文件校验失败，请稍后重试。");
        }

        if (File.Exists(localPath))
        {
            File.Delete(localPath);
        }

        File.Move(tempPath, localPath);
        return localPath;
    }

    private static async Task<bool> IsLocalFileValidAsync(
        string localPath,
        ChatMessage message,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(localPath))
        {
            return false;
        }

        if (message.FileSize is > 0)
        {
            var info = new FileInfo(localPath);
            if (info.Length != message.FileSize.Value)
            {
                return false;
            }
        }

        if (string.IsNullOrWhiteSpace(message.FileSha256))
        {
            return true;
        }

        var actualHash = await ComputeSha256HexAsync(localPath, cancellationToken);
        return actualHash.Equals(message.FileSha256, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> ComputeSha256HexAsync(string filePath, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
    }

    private static string? SanitizeFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        var trimmed = Path.GetFileName(fileName.Trim());
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return null;
        }

        foreach (var invalidChar in Path.GetInvalidFileNameChars())
        {
            trimmed = trimmed.Replace(invalidChar, '_');
        }

        return trimmed;
    }
}
