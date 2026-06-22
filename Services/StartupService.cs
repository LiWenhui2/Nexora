using System.IO;
using Microsoft.Win32;

namespace NaiwaProxy.Services;

public static class StartupService
{
    private const string RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Nexora";
    public const string SilentArgument = "--silent";

    public static bool IsEnabled()
    {
        var exePath = GetExecutablePath();
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return false;
        }

        using var key = Registry.CurrentUser.OpenSubKey(RegistryPath);
        var value = key?.GetValue(ValueName) as string;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var registryExePath = ExtractExecutablePath(value);
        return !string.IsNullOrWhiteSpace(registryExePath) &&
               string.Equals(NormalizePath(registryExePath), NormalizePath(exePath), StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsSilentEnabled()
    {
        if (!IsEnabled())
        {
            return false;
        }

        using var key = Registry.CurrentUser.OpenSubKey(RegistryPath);
        var value = key?.GetValue(ValueName) as string;
        return !string.IsNullOrWhiteSpace(value) &&
               value.Contains(SilentArgument, StringComparison.OrdinalIgnoreCase);
    }

    public static void SetStartup(bool enabled, bool silent)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: true)
            ?? throw new InvalidOperationException("无法打开 Windows 启动项注册表。");

        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        var exePath = GetExecutablePath()
            ?? throw new InvalidOperationException("无法定位当前程序路径，不能设置开机自启。");

        var command = silent
            ? $"\"{exePath}\" {SilentArgument}"
            : $"\"{exePath}\"";
        key.SetValue(ValueName, command, RegistryValueKind.String);
    }

    private static string? GetExecutablePath()
    {
        var path = Environment.ProcessPath;
        return string.IsNullOrWhiteSpace(path) ? null : path;
    }

    private static string? ExtractExecutablePath(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length >= 2 && trimmed.StartsWith('"'))
        {
            var endQuote = trimmed.IndexOf('"', 1);
            if (endQuote > 1)
            {
                return trimmed[1..endQuote];
            }
        }

        var spaceIndex = trimmed.IndexOf(' ');
        return spaceIndex > 0 ? trimmed[..spaceIndex] : trimmed;
    }

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return path;
        }
    }
}
