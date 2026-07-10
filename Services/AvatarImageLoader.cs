using System.IO;
using System.Net.Http;
using System.Windows.Media.Imaging;

namespace NaiwaProxy.Services;

public static class AvatarImageLoader
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    public static bool TryLoadLocal(string? url, out BitmapImage? bitmap)
    {
        bitmap = null;
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        var trimmed = url.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            bitmap = LoadBitmapFromFile(uri.LocalPath);
            return bitmap is not null;
        }

        if (LooksLikeLocalPath(trimmed))
        {
            bitmap = LoadBitmapFromFile(trimmed);
            return bitmap is not null;
        }

        return false;
    }

    public static bool TryLoad(string? url, out BitmapImage? bitmap)
    {
        try
        {
            bitmap = LoadBitmapAsync(url).GetAwaiter().GetResult();
            return bitmap is not null;
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Warning($"Avatar load failed for '{url}': {ex.Message}");
            bitmap = null;
            return false;
        }
    }

    public static async Task<BitmapImage?> LoadBitmapAsync(string? url, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var trimmed = url.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) &&
            uri.IsFile)
        {
            return LoadBitmapFromFile(uri.LocalPath);
        }

        if (LooksLikeLocalPath(trimmed))
        {
            return LoadBitmapFromFile(trimmed);
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out _) ||
            (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
             !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(RequestTimeout);

        using var response = await DirectHttpClientFactory.Shared.GetAsync(trimmed, timeoutSource.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(timeoutSource.Token).ConfigureAwait(false);
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, timeoutSource.Token).ConfigureAwait(false);
        memory.Position = 0;
        return CreateBitmapFromStream(memory);
    }

    public static async Task<BitmapImage?> LoadWithFallbackAsync(
        string? primaryUrl,
        string? fallbackUrl,
        CancellationToken cancellationToken = default)
    {
        var primary = await LoadBitmapAsync(primaryUrl, cancellationToken).ConfigureAwait(false);
        if (primary is not null)
        {
            return primary;
        }

        if (string.IsNullOrWhiteSpace(fallbackUrl) ||
            string.Equals(primaryUrl?.Trim(), fallbackUrl.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return await LoadBitmapAsync(fallbackUrl, cancellationToken).ConfigureAwait(false);
    }

    private static bool LooksLikeLocalPath(string path) =>
        Path.IsPathRooted(path) && File.Exists(path);

    private static BitmapImage? LoadBitmapFromFile(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return null;
        }

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(Path.GetFullPath(filePath), UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private static BitmapImage? CreateBitmapFromStream(Stream stream)
    {
        if (stream.Length == 0)
        {
            return null;
        }

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }
}
