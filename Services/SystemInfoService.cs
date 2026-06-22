using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace NaiwaProxy.Services;

public static class SystemInfoService
{
    private static readonly HttpClient DirectHttpClient = new(new HttpClientHandler
    {
        UseProxy = false
    })
    {
        Timeout = TimeSpan.FromSeconds(8)
    };

    public static string GetOperatingSystemDescription()
    {
        var architecture = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.X86 => "x86",
            Architecture.Arm64 => "ARM64",
            _ => RuntimeInformation.OSArchitecture.ToString()
        };

        return $"{GetWindowsDisplayName()} · {architecture}";
    }

    public static string GetSystemProxyAddress()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
        if (key is null)
        {
            return "未启用";
        }

        var enabled = key.GetValue("ProxyEnable");
        var server = key.GetValue("ProxyServer") as string;
        if (enabled is int value && value == 1 && !string.IsNullOrWhiteSpace(server))
        {
            return server.Trim();
        }

        var pacUrl = key.GetValue("AutoConfigURL") as string;
        if (!string.IsNullOrWhiteSpace(pacUrl))
        {
            return pacUrl.Trim();
        }

        return "未启用";
    }

    public static string GetHttpProxyAddressDisplay(int httpPort, bool allowLanAccess)
    {
        if (!allowLanAccess)
        {
            return $"127.0.0.1:{httpPort}";
        }

        var lanIp = GetPrimaryLanIPv4();
        return lanIp is null ? $"0.0.0.0:{httpPort}" : $"{lanIp}:{httpPort}";
    }

    public static string? GetPrimaryLanIPv4()
    {
        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (networkInterface.OperationalStatus != OperationalStatus.Up ||
                networkInterface.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            foreach (var address in networkInterface.GetIPProperties().UnicastAddresses)
            {
                if (address.Address.AddressFamily != AddressFamily.InterNetwork ||
                    IPAddress.IsLoopback(address.Address))
                {
                    continue;
                }

                var bytes = address.Address.GetAddressBytes();
                if (bytes[0] == 169 && bytes[1] == 254)
                {
                    continue;
                }

                return address.Address.ToString();
            }
        }

        return null;
    }

    public static async Task<string> GetLocalPublicIpAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var ip = (await DirectHttpClient.GetStringAsync("https://api.ipify.org", cancellationToken)).Trim();
            return string.IsNullOrWhiteSpace(ip) ? "获取失败" : ip;
        }
        catch
        {
            try
            {
                var ip = (await DirectHttpClient.GetStringAsync("https://ipv4.icanhazip.com", cancellationToken)).Trim();
                return string.IsNullOrWhiteSpace(ip) ? "获取失败" : ip;
            }
            catch
            {
                return "获取失败";
            }
        }
    }

    private static string GetWindowsDisplayName()
    {
        var build = GetWindowsBuildNumber();
        if (build >= 22000)
        {
            return "Windows 11";
        }

        if (build >= 10240)
        {
            return "Windows 10";
        }

        if (build > 0)
        {
            return $"Windows {Environment.OSVersion.Version.Major}";
        }

        return "Windows";
    }

    private static int GetWindowsBuildNumber()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var buildText = key?.GetValue("CurrentBuildNumber") as string
                ?? key?.GetValue("CurrentBuild")?.ToString();
            if (int.TryParse(buildText, out var build))
            {
                return build;
            }
        }
        catch
        {
        }

        return Environment.OSVersion.Version.Build;
    }
}
