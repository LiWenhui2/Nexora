using System.IO;
using System.Text.RegularExpressions;

namespace NaiwaProxy.Services;

public static partial class ThemeBackgroundService
{
    private static readonly string ThemeBackgroundsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Nexora",
        "theme-backgrounds");

    [GeneratedRegex(@"[^\w\-. ]+", RegexOptions.CultureInvariant)]
    private static partial Regex InvalidFileNameCharsRegex();

    public static string GetThemeBackgroundsDirectory()
    {
        Directory.CreateDirectory(ThemeBackgroundsDirectory);
        return ThemeBackgroundsDirectory;
    }

    public static string ImportFromFile(string sourcePath)
    {
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("图片文件不存在。", sourcePath);
        }

        var extension = Path.GetExtension(sourcePath);
        if (!IsSupportedExtension(extension))
        {
            throw new InvalidOperationException("仅支持 JPG、PNG、WebP、BMP 格式。");
        }

        var directory = GetThemeBackgroundsDirectory();
        var fileName = $"local-{DateTime.UtcNow:yyyyMMddHHmmssfff}{extension.ToLowerInvariant()}";
        var destination = Path.Combine(directory, fileName);
        File.Copy(sourcePath, destination, overwrite: true);
        return destination;
    }

    public static string? ResolveAbsolutePath(string? settingsPath)
    {
        if (string.IsNullOrWhiteSpace(settingsPath))
        {
            return null;
        }

        var fullPath = Path.GetFullPath(settingsPath.Trim());
        return File.Exists(fullPath) ? fullPath : null;
    }

    private static bool IsSupportedExtension(string extension) =>
        extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".webp", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase);
}
