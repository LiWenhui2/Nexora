using System.IO;
using System.Security.Principal;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using NaiwaProxy.Models;

namespace NaiwaProxy.Services;

public static class TunService
{
    private static Process? _process;
    private static readonly object Sync = new();

    public static string RuntimeDirectory => Path.Combine(AppContext.BaseDirectory, "cores");
    public static string WintunPath => Path.Combine(RuntimeDirectory, "wintun.dll");
    public static string SingBoxPath => Path.Combine(RuntimeDirectory, "sing-box.exe");
    public static string ConfigPath
    {
        get
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Nexora");
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

    public static void Start(AppSettings settings, VmessProfile profile)
    {
        EnsureCanEnable();
        Stop();
        File.WriteAllText(ConfigPath, BuildConfig(settings, profile));
        ValidateConfig();

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = SingBoxPath,
                Arguments = $"run -c \"{ConfigPath}\"",
                WorkingDirectory = RuntimeDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            },
            EnableRaisingEvents = true
        };
        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) => AppendProcessOutput(output, e.Data);
        process.ErrorDataReceived += (_, e) => AppendProcessOutput(output, e.Data);
        process.Exited += (_, _) =>
        {
            lock (Sync)
            {
                if (!ReferenceEquals(_process, process))
                {
                    return;
                }

                _process = null;
            }

            var detail = output.ToString().Trim();
            DiagnosticLogService.Error(
                $"TUN runtime exited unexpectedly (code {SafeExitCode(process)})." +
                (detail.Length == 0 ? string.Empty : $"{Environment.NewLine}{detail}"));
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        lock (Sync)
        {
            _process = process;
        }

        if (process.WaitForExit(1500))
        {
            var detail = output.ToString().Trim();
            lock (Sync)
            {
                if (ReferenceEquals(_process, process))
                {
                    _process = null;
                }
            }

            process.Dispose();
            throw new InvalidOperationException(
                "TUN 运行时启动后立即退出。" +
                (detail.Length == 0 ? string.Empty : $"{Environment.NewLine}{detail}"));
        }

        DiagnosticLogService.Info(
            $"TUN started via sing-box (SOCKS5 -> 127.0.0.1:{settings.SocksPort}, excluded node={profile.Address}).");
    }

    public static void Stop()
    {
        Process? process;
        lock (Sync)
        {
            process = _process;
            _process = null;
        }

        if (process is null) return;

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
            }
        }
        catch
        {
            // Best effort cleanup for TUN runtime.
        }
        finally
        {
            process.Dispose();
        }
    }

    private static string BuildConfig(AppSettings settings, VmessProfile profile)
    {
        var excludedAddresses = BuildRouteExclusions(profile.Address);
        var config = new
        {
            log = new
            {
                level = "warn"
            },
            dns = new
            {
                servers = new object[]
                {
                    new
                    {
                        type = "udp",
                        tag = "local-dns",
                        server = "223.5.5.5",
                        server_port = 53
                    }
                },
                final = "local-dns",
                strategy = "prefer_ipv4"
            },
            inbounds = new object[]
            {
                new
                {
                    type = "tun",
                    tag = "tun-in",
                    interface_name = "NexoraTun",
                    address = new[] { "172.19.0.1/30" },
                    auto_route = true,
                    strict_route = true,
                    stack = "system",
                    mtu = 1500,
                    route_exclude_address = excludedAddresses
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
                default_domain_resolver = "local-dns",
                final = "proxy",
                rules = new object[]
                {
                    new
                    {
                        action = "sniff"
                    },
                    new
                    {
                        protocol = "dns",
                        action = "hijack-dns"
                    },
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

    private static string[] BuildRouteExclusions(string nodeAddress)
    {
        var exclusions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "127.0.0.0/8", "::1/128" };
        try
        {
            var addresses = IPAddress.TryParse(nodeAddress, out var parsed)
                ? [parsed]
                : Dns.GetHostAddressesAsync(nodeAddress).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            foreach (var address in addresses)
            {
                exclusions.Add(address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                    ? $"{address}/32"
                    : $"{address}/128");
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"无法解析代理节点地址 {nodeAddress}，为避免 TUN 流量回环，已取消启动。", ex);
        }

        return [.. exclusions];
    }

    private static void ValidateConfig()
    {
        using var check = Process.Start(new ProcessStartInfo
        {
            FileName = SingBoxPath,
            Arguments = $"check -c \"{ConfigPath}\"",
            WorkingDirectory = RuntimeDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        }) ?? throw new InvalidOperationException("无法启动 sing-box 配置检查。");
        var stdoutTask = check.StandardOutput.ReadToEndAsync();
        var stderrTask = check.StandardError.ReadToEndAsync();
        if (!check.WaitForExit(10000))
        {
            check.Kill(entireProcessTree: true);
            throw new TimeoutException("sing-box 配置检查超时。");
        }

        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();
        if (check.ExitCode != 0)
        {
            var detail = string.Join(Environment.NewLine, new[] { stdout, stderr }.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
            throw new InvalidOperationException($"sing-box 配置无效。{Environment.NewLine}{detail}");
        }
    }

    private static void AppendProcessOutput(StringBuilder output, string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        lock (output) output.AppendLine(line);
    }

    private static int SafeExitCode(Process process)
    {
        try { return process.ExitCode; }
        catch { return -1; }
    }

    public static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }
}
