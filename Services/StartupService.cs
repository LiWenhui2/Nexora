using System.IO;
using Microsoft.Win32;

namespace NaiwaProxy.Services;

public static class StartupService
{
    private const string RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "NaiwaProxy";

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

        return string.Equals(NormalizePath(Unquote(value)), NormalizePath(exePath), StringComparison.OrdinalIgnoreCase);
    }

    public static void SetEnabled(bool enabled)
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

        key.SetValue(ValueName, $"\"{exePath}\"", RegistryValueKind.String);
    }

    private static string? GetExecutablePath()
    {
        var path = Environment.ProcessPath;
        return string.IsNullOrWhiteSpace(path) ? null : path;
    }

    private static string Unquote(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length >= 2 && trimmed.StartsWith('"') && trimmed.EndsWith('"'))
        {
            return trimmed[1..^1];
        }

        return trimmed;
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
