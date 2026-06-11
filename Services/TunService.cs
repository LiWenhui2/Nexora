using System.IO;
using System.Security.Principal;
using System.Diagnostics;
using System.Text.Json;
using NaiwaProxy.Models;

namespace NaiwaProxy.Services;

public static class TunService
{
    private static Process? _process;

    public static string RuntimeDirectory => Path.Combine(AppContext.BaseDirectory, "cores");
    public static string WintunPath => Path.Combine(RuntimeDirectory, "wintun.dll");
    public static string SingBoxPath => Path.Combine(RuntimeDirectory, "sing-box.exe");
    public static string ConfigPath
    {
        get
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "NaiwaProxy");
            Directory.CreateDirectory(directory);
            return Path.Combine(directory, "sing-box-tun.json");
        }
    }

    public static bool HasWintun => File.Exists(WintunPath);
    public static bool HasTunRuntime => File.Exists(SingBoxPath);
    public static bool IsRunning => _process is { HasExited: false };

    public static string GetStatusText()
    {
        if (!IsAdministrator())
        {
            return "需要管理员";
        }

        if (!HasWintun)
        {
            return "缺少 wintun";
        }

        if (!HasTunRuntime)
        {
            return "缺少运行时";
        }

        return "可启用";
    }

    public static void EnsureCanEnable()
    {
        if (!IsAdministrator())
        {
            throw new InvalidOperationException("TUN 模式需要以管理员身份运行。");
        }

        if (!HasWintun)
        {
            throw new FileNotFoundException($"缺少 TUN 驱动文件：{WintunPath}");
        }

        if (!HasTunRuntime)
        {
            throw new FileNotFoundException(
                $"缺少 TUN 转发运行时。请将 sing-box.exe 放入：{RuntimeDirectory}");
        }
    }

    public static void Start(AppSettings settings)
    {
        EnsureCanEnable();
        Stop();
        File.WriteAllText(ConfigPath, BuildConfig(settings));

        _process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = SingBoxPath,
                Arguments = $"run -c \"{ConfigPath}\"",
                WorkingDirectory = RuntimeDirectory,
                UseShellExecute = false,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };
        _process.Start();
    }

    public static void Stop()
    {
        if (_process is null)
        {
            return;
        }

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(3000);
            }
        }
        catch
        {
            // Best effort cleanup for TUN runtime.
        }
        finally
        {
            _process.Dispose();
            _process = null;
        }
    }

    private static string BuildConfig(AppSettings settings)
    {
        var config = new
        {
            log = new
            {
                level = "warn"
            },
            inbounds = new object[]
            {
                new
                {
                    type = "tun",
                    tag = "tun-in",
                    interface_name = "NaiwaProxyTun",
                    address = new[] { "172.19.0.1/30" },
                    auto_route = true,
                    strict_route = true,
                    stack = "system",
                    sniff = true
                }
            },
            outbounds = new object[]
            {
                new
                {
                    type = "socks",
                    tag = "proxy",
                    server = "127.0.0.1",
                    server_port = settings.SocksPort,
                    version = "5"
                },
                new
                {
                    type = "direct",
                    tag = "direct"
                }
            },
            route = new
            {
                auto_detect_interface = true,
                final = "proxy",
                rules = new object[]
                {
                    new
                    {
                        ip_cidr = new[]
                        {
                            "10.0.0.0/8",
                            "172.16.0.0/12",
                            "192.168.0.0/16",
                            "127.0.0.0/8",
                            "224.0.0.0/4"
                        },
                        outbound = "direct"
                    }
                }
            }
        };

        return JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
    }

    public static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }
}
