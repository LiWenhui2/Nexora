using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using NaiwaProxy.Models;

namespace NaiwaProxy.Services;

public sealed class AppUpdateDownloadService
{
    public async Task<string> DownloadAsync(
        AppUpdateRelease release,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (release.File is null || string.IsNullOrWhiteSpace(release.File.DownloadUrl))
        {
            throw new InvalidOperationException("当前版本没有可用的下载地址。");
        }

        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Nexora",
            "updates");
        Directory.CreateDirectory(directory);
        var targetPath = Path.Combine(directory, release.File.Filename);

        using var response = await DirectHttpClientFactory.Shared.GetAsync(
            release.File.DownloadUrl,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? release.File.FileSize;
        progress?.Report(new DownloadProgress(0, totalBytes));

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using (var target = File.Create(targetPath))
        {
            var buffer = new byte[81920];
            long downloadedBytes = 0;
            int bytesRead;
            while ((bytesRead = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                downloadedBytes += bytesRead;
                progress?.Report(new DownloadProgress(downloadedBytes, totalBytes));
            }

            await target.FlushAsync(cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(release.File.Sha256))
        {
            File.Delete(targetPath);
            throw new InvalidOperationException("更新元数据缺少 SHA-256，已拒绝安装不受校验的文件。");
        }
        else
        {
            var actualHash = await ComputeSha256HexAsync(targetPath, cancellationToken);
            if (!actualHash.Equals(release.File.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(targetPath);
                throw new InvalidOperationException("安装包 SHA-256 校验失败，文件可能已损坏，请重新下载。");
            }
        }

        UpdateSecurityService.VerifyAuthenticode(targetPath, release.File.SignatureThumbprint);

        return targetPath;
    }

    private static async Task<string> ComputeSha256HexAsync(string filePath, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
    }
}

public readonly record struct DownloadProgress(long DownloadedBytes, long TotalBytes);
