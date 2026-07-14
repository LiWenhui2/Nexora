using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using NaiwaProxy.Models;

namespace NaiwaProxy.Services;

/// <summary>
/// Provides a narrowly-scoped TUN network layer for the packaged ChatGPT/Codex
/// client. Windows HTTP proxy settings do not cover every UDP/QUIC path used by
/// Chromium, so those paths must be captured below WinINET. Non-OpenAI traffic
/// remains direct and the regular user-facing TUN mode stays independent.
/// </summary>
public static class OpenAiNetworkLayerService
{
    private static readonly object Sync = new();
    private static Process? _process;

    public static bool IsRunning => _process is { HasExited: false };
    public static DateTime? LastStartedAtUtc { get; private set; }
    public static string? LastError { get; private set; }

    internal static void RecordFailure(Exception exception) => LastError = exception.Message;

    private static string ConfigPath
    {
        get
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Nexora");
            Directory.CreateDirectory(directory);
            return Path.Combine(directory, "sing-box-openai.json");
        }
    }

    public static void Start(AppSettings settings, VmessProfile profile)
    {
        LastError = null;
        if (!TunService.IsAdministrator())
        {
            throw new InvalidOperationException("ChatGPT Codex 网络层优化需要管理员权限。");
        }

        if (!TunService.HasWintun || !TunService.HasTunRuntime)
        {
            throw new InvalidOperationException("缺少 ChatGPT Codex 网络层优化所需的 sing-box 或 Wintun 组件。");
        }

        Stop();
        File.WriteAllText(ConfigPath, BuildConfig(settings, profile));
        ValidateConfig();

        var output = new StringBuilder();
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = TunService.SingBoxPath,
                Arguments = $"run -c \"{ConfigPath}\"",
                WorkingDirectory = TunService.RuntimeDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            },
            EnableRaisingEvents = true
        };
        process.OutputDataReceived += (_, e) => AppendOutput(output, e.Data);
        process.ErrorDataReceived += (_, e) => AppendOutput(output, e.Data);
        process.Exited += (_, _) => HandleUnexpectedExit(process, output);

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
                "ChatGPT Codex 专用网络层启动失败。" +
                (detail.Length == 0 ? string.Empty : $"{Environment.NewLine}{detail}"));
        }

        DiagnosticLogService.Info(
            $"ChatGPT Codex dedicated network layer started (SOCKS5 -> 127.0.0.1:{settings.SocksPort}).");
        LastStartedAtUtc = DateTime.UtcNow;
    }

    public static void Stop()
    {
        Process? process;
        lock (Sync)
        {
            process = _process;
            _process = null;
        }

        if (process is null)
        {
            return;
        }

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
            // Best effort cleanup while the proxy core or application exits.
        }
        finally
        {
            process.Dispose();
        }
    }

    private static string BuildConfig(AppSettings settings, VmessProfile profile)
    {
        var openAiDomains = OpenAiCodexOptimizationService.ProxyDomains
            .Select(domain => domain.Trim().TrimStart('.'))
            .Where(domain => domain.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var config = new
        {
            log = new { level = "warn" },
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
                    tag = "openai-tun-in",
                    interface_name = "NexoraOpenAI",
                    address = new[] { "172.20.0.1/30" },
                    auto_route = true,
                    strict_route = true,
                    stack = "system",
                    mtu = 1500,
                    route_exclude_address = BuildRouteExclusions(profile.Address)
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
                new { type = "direct", tag = "direct" }
            },
            route = new
            {
                auto_detect_interface = true,
                default_domain_resolver = "local-dns",
                final = "direct",
                rules = new object[]
                {
                    new { action = "sniff" },
                    new { protocol = "dns", action = "hijack-dns" },
                    new
                    {
                        process_name = new[] { "ChatGPT.exe", "chatgpt.exe", "Codex.exe", "codex.exe" },
                        outbound = "proxy"
                    },
                    new
                    {
                        domain_suffix = openAiDomains,
                        outbound = "proxy"
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
        var exclusions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "127.0.0.0/8",
            "::1/128"
        };

        try
        {
            var addresses = IPAddress.TryParse(nodeAddress, out var parsed)
                ? [parsed]
                : Dns.GetHostAddressesAsync(nodeAddress)
                    .WaitAsync(TimeSpan.FromSeconds(5))
                    .GetAwaiter()
                    .GetResult();
            foreach (var address in addresses)
            {
                exclusions.Add(address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                    ? $"{address}/32"
                    : $"{address}/128");
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"无法解析代理节点地址 {nodeAddress}。", ex);
        }

        return [.. exclusions];
    }

    private static void ValidateConfig()
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = TunService.SingBoxPath,
            Arguments = $"check -c \"{ConfigPath}\"",
            WorkingDirectory = TunService.RuntimeDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        }) ?? throw new InvalidOperationException("无法启动 sing-box 配置检查。");

        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"ChatGPT Codex 专用网络层配置无效：{(error + output).Trim()}");
        }
    }

    private static void HandleUnexpectedExit(Process process, StringBuilder output)
    {
        lock (Sync)
        {
            if (!ReferenceEquals(_process, process))
            {
                return;
            }

            _process = null;
        }

        DiagnosticLogService.Error(
            $"ChatGPT Codex dedicated network layer exited unexpectedly (code {SafeExitCode(process)}). " +
            output.ToString().Trim());
        LastError = output.ToString().Trim();
    }

    private static void AppendOutput(StringBuilder output, string? line)
    {
        if (!string.IsNullOrWhiteSpace(line))
        {
            lock (output)
            {
                output.AppendLine(line);
            }
        }
    }

    private static int SafeExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch
        {
            return -1;
        }
    }
}
