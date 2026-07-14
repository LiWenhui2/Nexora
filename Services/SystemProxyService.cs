using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace NaiwaProxy.Services;

public static class SystemProxyService
{
    private const string RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private const int InternetOptionSettingsChanged = 39;
    private const int InternetOptionRefresh = 37;

    public static void EnableHttpProxy(int httpPort, bool enableUwpOptimization)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: true)
            ?? throw new InvalidOperationException("Cannot open Windows Internet Settings registry key.");

        key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
        key.SetValue("ProxyServer", $"127.0.0.1:{httpPort}", RegistryValueKind.String);
        key.SetValue("ProxyOverride", BuildProxyOverride(enableUwpOptimization), RegistryValueKind.String);
        TryDeleteValue(key, "AutoConfigURL");
        NotifyProxySettingsChanged();
    }

    public static void EnablePacProxy(int httpPort, bool enableUwpOptimization)
    {
        var pacPath = WritePacFile(httpPort, enableUwpOptimization);
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

    public static bool IsHttpProxyEnabled(int httpPort)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryPath);
        if (key is null)
        {
            return false;
        }

        var enabled = key.GetValue("ProxyEnable");
        var server = key.GetValue("ProxyServer") as string;
        return enabled is int value && value == 1 &&
               string.Equals(server, $"127.0.0.1:{httpPort}", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsPacProxyEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryPath);
        if (key is null)
        {
            return false;
        }

        var pacUrl = key.GetValue("AutoConfigURL") as string;
        return !string.IsNullOrWhiteSpace(pacUrl);
    }

    public static bool IsProxyActive(string mode, int httpPort)
    {
        return mode switch
        {
            "Auto" => IsHttpProxyEnabled(httpPort),
            "Pac" => IsPacProxyEnabled(),
            _ => false
        };
    }

    public static async Task PrepareOpenAiDesktopProxyAsync(
        int httpPort,
        bool enableUwpOptimization,
        CancellationToken cancellationToken = default)
    {
        await EnsureOpenAiLoopbackExemptionsAsync(cancellationToken);
        RefreshHttpProxy(httpPort, enableUwpOptimization);
        await Task.Delay(250, cancellationToken);
        NotifyProxySettingsChanged();
    }

    public static async Task ConfigureUwpLoopbackExemptionsAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        var packageFamilies = DiscoverUwpOptimizationPackageFamilyNames();
        var failedPackages = new List<string>();
        foreach (var packageFamilyName in packageFamilies)
        {
            if (!await ConfigureLoopbackExemptionAsync(packageFamilyName, enabled, cancellationToken) && enabled)
            {
                failedPackages.Add(packageFamilyName);
            }
        }

        if (failedPackages.Count > 0)
        {
            throw new InvalidOperationException(
                $"无法为 {failedPackages.Count} 个 UWP 应用写入本地回环权限，请确认 Nexora 已使用管理员权限运行。");
        }

        DiagnosticLogService.Info(
            $"UWP loopback optimization {(enabled ? "enabled" : "disabled")} for {packageFamilies.Count} package families.");
        NotifyProxySettingsChanged();
    }

    public static void RefreshHttpProxy(int httpPort, bool enableUwpOptimization)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: true)
            ?? throw new InvalidOperationException("Cannot open Windows Internet Settings registry key.");

        key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
        key.SetValue("ProxyServer", $"127.0.0.1:{httpPort}", RegistryValueKind.String);
        key.SetValue("ProxyOverride", BuildProxyOverride(enableUwpOptimization), RegistryValueKind.String);
        TryDeleteValue(key, "AutoConfigURL");
        NotifyProxySettingsChanged();
    }

    private static string WritePacFile(int httpPort, bool enableUwpOptimization)
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Nexora");
        Directory.CreateDirectory(directory);

        var pacPath = Path.Combine(directory, "proxy.pac");
        var microsoftDirectRules = enableUwpOptimization
            ? """
      shExpMatch(host, "*.microsoft.com") ||
      shExpMatch(host, "*.windows.com") ||
      shExpMatch(host, "*.live.com") ||
      shExpMatch(host, "*.microsoftonline.com") ||
      shExpMatch(host, "*.xboxlive.com") ||
      shExpMatch(host, "*.mp.microsoft.com") ||
      shExpMatch(host, "msftconnecttest.com") ||
      shExpMatch(host, "msftncsi.com") ||
"""
            : "";
        var pac = $$"""
function FindProxyForURL(url, host) {
  if (isPlainHostName(host) ||
      shExpMatch(host, "*.local") ||
{{microsoftDirectRules}}
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

    private static async Task EnsureOpenAiLoopbackExemptionsAsync(CancellationToken cancellationToken)
    {
        foreach (var packageFamilyName in DiscoverOpenAiPackageFamilyNames())
        {
            await ConfigureLoopbackExemptionAsync(packageFamilyName, true, cancellationToken);
        }
    }

    private static async Task<bool> ConfigureLoopbackExemptionAsync(
        string packageFamilyName,
        bool enabled,
        CancellationToken cancellationToken)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "CheckNetIsolation.exe",
                Arguments = $"LoopbackExempt -{(enabled ? "a" : "d")} -n={packageFamilyName}",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });

            if (process is null)
            {
                return false;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            await process.WaitForExitAsync(timeout.Token);
            if (process.ExitCode != 0)
            {
                var error = await process.StandardError.ReadToEndAsync(cancellationToken);
                var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
                DiagnosticLogService.Warning(
                    $"CheckNetIsolation failed for {packageFamilyName} ({process.ExitCode}): {error} {output}".Trim());
                return false;
            }

            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            DiagnosticLogService.Warning($"Timed out updating loopback exemption for {packageFamilyName}.");
            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Warning(
                $"Failed to update UWP loopback exemption for {packageFamilyName}: {ex.Message}");
            return false;
        }
    }

    private static IReadOnlyCollection<string> DiscoverUwpOptimizationPackageFamilyNames()
    {
        var packageFamilies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var packagesDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Packages");
            if (!Directory.Exists(packagesDirectory))
            {
                return packageFamilies;
            }

            foreach (var directory in Directory.EnumerateDirectories(packagesDirectory))
            {
                var familyName = Path.GetFileName(directory);
                if (UwpOptimizationPackagePrefixes.Any(prefix =>
                        familyName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                {
                    packageFamilies.Add(familyName);
                }
            }
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Warning($"Failed to discover UWP package identities: {ex.Message}");
        }

        return packageFamilies;
    }

    private static IReadOnlyCollection<string> DiscoverOpenAiPackageFamilyNames()
    {
        var packageFamilies = new HashSet<string>(OpenAiPackageFamilyNames, StringComparer.OrdinalIgnoreCase);
        try
        {
            var packagesDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Packages");
            if (!Directory.Exists(packagesDirectory))
            {
                return packageFamilies;
            }

            foreach (var directory in Directory.EnumerateDirectories(packagesDirectory))
            {
                var familyName = Path.GetFileName(directory);
                if (familyName.Contains("OpenAI", StringComparison.OrdinalIgnoreCase) ||
                    familyName.Contains("ChatGPT", StringComparison.OrdinalIgnoreCase) ||
                    familyName.Contains("Codex", StringComparison.OrdinalIgnoreCase))
                {
                    packageFamilies.Add(familyName);
                }
            }
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Warning($"Failed to discover OpenAI package identities: {ex.Message}");
        }

        return packageFamilies;
    }

    private static readonly string[] OpenAiPackageFamilyNames =
    [
        "OpenAI.Codex_2p2nqsd0c76g0"
    ];

    private static readonly string[] UwpOptimizationPackagePrefixes =
    [
        "Microsoft.WindowsStore_",
        "Microsoft.StorePurchaseApp_",
        "Microsoft.DesktopAppInstaller_",
        "Microsoft.GamingApp_",
        "Microsoft.XboxIdentityProvider_",
        "Microsoft.XboxApp_"
    ];

    private static readonly string[] MicrosoftStoreProxyOverrideRules =
    [
        "*.microsoft.com",
        "*.windows.com",
        "*.live.com",
        "*.microsoftonline.com",
        "*.xboxlive.com",
        "*.mp.microsoft.com",
        "msftconnecttest.com",
        "msftncsi.com"
    ];

    private static string BuildProxyOverride(bool enableUwpOptimization) =>
        enableUwpOptimization
            ? string.Join(';', MicrosoftStoreProxyOverrideRules.Append("<local>"))
            : "<local>";

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
