using Microsoft.Win32;
using System.IO;
using System.Runtime.InteropServices;

namespace NaiwaProxy.Services;

public static class SystemProxyService
{
    private const string RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private const int InternetOptionSettingsChanged = 39;
    private const int InternetOptionRefresh = 37;

    public static void EnableHttpProxy(int httpPort)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: true)
            ?? throw new InvalidOperationException("Cannot open Windows Internet Settings registry key.");

        key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
        key.SetValue("ProxyServer", $"127.0.0.1:{httpPort}", RegistryValueKind.String);
        key.SetValue("ProxyOverride", "<local>", RegistryValueKind.String);
        TryDeleteValue(key, "AutoConfigURL");
        NotifyProxySettingsChanged();
    }

    public static void EnablePacProxy(int httpPort)
    {
        var pacPath = WritePacFile(httpPort);
        using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: true)
            ?? throw new InvalidOperationException("Cannot open Windows Internet Settings registry key.");

        key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
        TryDeleteValue(key, "ProxyServer");
        key.SetValue("AutoConfigURL", new Uri(pacPath).AbsoluteUri, RegistryValueKind.String);
        NotifyProxySettingsChanged();
    }

    public static void DisableProxy()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: true)
            ?? throw new InvalidOperationException("Cannot open Windows Internet Settings registry key.");

        key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
        TryDeleteValue(key, "ProxyServer");
        TryDeleteValue(key, "AutoConfigURL");
        NotifyProxySettingsChanged();
    }

    private static string WritePacFile(int httpPort)
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "NaiwaProxy");
        Directory.CreateDirectory(directory);

        var pacPath = Path.Combine(directory, "proxy.pac");
        var pac = $$"""
function FindProxyForURL(url, host) {
  if (isPlainHostName(host) ||
      shExpMatch(host, "*.local") ||
      isInNet(dnsResolve(host), "10.0.0.0", "255.0.0.0") ||
      isInNet(dnsResolve(host), "172.16.0.0", "255.240.0.0") ||
      isInNet(dnsResolve(host), "192.168.0.0", "255.255.0.0") ||
      isInNet(dnsResolve(host), "127.0.0.0", "255.0.0.0")) {
    return "DIRECT";
  }
  return "PROXY 127.0.0.1:{{httpPort}}; DIRECT";
}
""";
        File.WriteAllText(pacPath, pac);
        return pacPath;
    }

    private static void TryDeleteValue(RegistryKey key, string name)
    {
        try
        {
            key.DeleteValue(name, throwOnMissingValue: false);
        }
        catch
        {
            // Best effort cleanup for existing Windows proxy values.
        }
    }

    private static void NotifyProxySettingsChanged()
    {
        InternetSetOption(IntPtr.Zero, InternetOptionSettingsChanged, IntPtr.Zero, 0);
        InternetSetOption(IntPtr.Zero, InternetOptionRefresh, IntPtr.Zero, 0);
    }

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);
}
