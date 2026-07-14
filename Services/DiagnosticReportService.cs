using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using NaiwaProxy.Models;

namespace NaiwaProxy.Services;

public sealed record DiagnosticCheck(string Name, bool Success, string Detail);

public sealed record DiagnosticResult(
    DateTime CompletedAt,
    RuntimeHealthSnapshot Runtime,
    IReadOnlyList<DiagnosticCheck> Checks)
{
    public bool Success => Checks.All(check => check.Success);
    public string Summary => Success ? "全部检查通过" : $"发现 {Checks.Count(check => !check.Success)} 项异常";
}

public static partial class DiagnosticReportService
{
    public static async Task<DiagnosticResult> RunAsync(
        AppSettings settings,
        VmessProfile? activeProfile,
        bool coreRunning,
        bool proxyDesiredRunning,
        CancellationToken cancellationToken = default)
    {
        var runtime = await RuntimeHealthService.CheckAsync(
            settings,
            coreRunning,
            proxyDesiredRunning,
            cancellationToken);
        var checks = new List<DiagnosticCheck>
        {
            new("系统网络", runtime.NetworkAvailable, runtime.NetworkAvailable ? "可用" : "Windows 报告网络不可用"),
            new("Xray 核心", runtime.CoreRunning, runtime.CoreRunning ? "运行中" : "未运行"),
            new("HTTP 端口", runtime.HttpPortReady, $"127.0.0.1:{settings.HttpPort}"),
            new("SOCKS 端口", runtime.SocksPortReady, $"127.0.0.1:{settings.SocksPort}"),
            new("API 端口", runtime.ApiPortReady, $"127.0.0.1:{settings.ApiPort}"),
            new("系统代理", runtime.SystemProxyConsistent, runtime.SystemProxyConsistent ? "与应用设置一致" : "被清除或被其他软件修改"),
            new("普通 TUN", !runtime.TunExpected || runtime.TunRunning, runtime.TunExpected ? (runtime.TunRunning ? "运行中" : "应运行但未启动") : "未启用"),
            new("Codex 专用网络层", !runtime.OpenAiLayerExpected || runtime.OpenAiLayerRunning,
                runtime.OpenAiLayerExpected ? (runtime.OpenAiLayerRunning ? "运行中" : OpenAiNetworkLayerService.LastError ?? "应运行但未启动") : "未启用"),
            new("UWP 优化", !settings.UwpOptimizationEnabled || TunService.IsAdministrator(),
                settings.UwpOptimizationEnabled
                    ? (TunService.IsAdministrator() ? "已启用 AppContainer 本地回环优化" : "已启用，但当前缺少管理员权限")
                    : "未启用"),
            new("管理员权限", !runtime.OpenAiLayerExpected && !runtime.TunExpected && !settings.UwpOptimizationEnabled || TunService.IsAdministrator(),
                TunService.IsAdministrator() ? "已获取" : "当前为普通权限")
        };

        var corePath = ResolveCorePath(settings.CoreExecutable);
        checks.Add(new DiagnosticCheck("Xray 文件", File.Exists(corePath), corePath));
        checks.Add(new DiagnosticCheck("sing-box 文件", TunService.HasTunRuntime, TunService.SingBoxPath));
        checks.Add(new DiagnosticCheck("Wintun 文件", TunService.HasWintun, TunService.WintunPath));

        var compatibility = await ComponentCompatibilityService.CheckAsync(cancellationToken);
        checks.Add(new DiagnosticCheck(
            "核心组件兼容性",
            compatibility.Success,
            string.Join("；", compatibility.Details)));
        var dnsDiagnostics = await DnsDiagnosticsService.RunAsync(settings, cancellationToken);
        checks.Add(new DiagnosticCheck(
            "DNS、IPv6 与泄漏风险",
            dnsDiagnostics.Success,
            string.Join("；", dnsDiagnostics.Details)));
        var backups = ConfigurationBackupService.ListBackups();
        checks.Add(new DiagnosticCheck(
            "配置备份",
            backups.Count > 0,
            backups.Count > 0 ? $"已保留 {backups.Count} 个备份" : "尚未创建备份"));

        try
        {
            var addresses = await Dns.GetHostAddressesAsync("chatgpt.com", cancellationToken);
            checks.Add(new DiagnosticCheck(
                "DNS 解析",
                addresses.Length > 0,
                addresses.Length > 0 ? string.Join(", ", addresses.Take(3)) : "未返回地址"));
        }
        catch (Exception ex)
        {
            checks.Add(new DiagnosticCheck("DNS 解析", false, ex.Message));
        }

        if (activeProfile is not null)
        {
            var latency = await LatencyTestService.MeasureTcpAsync(
                activeProfile.Address,
                activeProfile.Port,
                5000,
                cancellationToken);
            checks.Add(new DiagnosticCheck(
                "当前节点 TCP",
                latency is not null,
                latency is int ms ? $"{activeProfile.Address}:{activeProfile.Port}，{ms} ms" : $"{activeProfile.Address}:{activeProfile.Port}，连接失败"));
        }
        else
        {
            checks.Add(new DiagnosticCheck("当前节点 TCP", false, "未选择活动节点"));
        }

        if (coreRunning && runtime.HttpPortReady)
        {
            var openAi = await WebsiteConnectivityTestService.TestAsync(
                "https://chatgpt.com",
                settings.HttpPort,
                10000,
                cancellationToken);
            checks.Add(new DiagnosticCheck(
                "ChatGPT 代理访问",
                openAi.Success,
                openAi.Success ? $"成功，{openAi.LatencyMs} ms" : openAi.ErrorMessage ?? "失败"));
        }
        else
        {
            checks.Add(new DiagnosticCheck("ChatGPT 代理访问", false, "代理核心或 HTTP 端口未就绪"));
        }

        return new DiagnosticResult(DateTime.Now, runtime, checks);
    }

    public static string ExportSupportBundle(
        DiagnosticResult result,
        AppSettings settings,
        VmessProfile? activeProfile,
        string destinationPath)
    {
        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (File.Exists(destinationPath))
        {
            File.Delete(destinationPath);
        }

        using var archive = ZipFile.Open(destinationPath, ZipArchiveMode.Create);
        var sensitiveValues = BuildSensitiveValues(settings);
        WriteEntry(archive, "diagnostic-report.txt", BuildReport(result, settings, activeProfile));
        WriteEntry(archive, "runtime-summary.txt", BuildRuntimeSummary(settings, activeProfile));
        AddSanitizedLogTail(archive, DiagnosticLogService.AppLogPath, "logs/app-tail.log", 1500, sensitiveValues);
        AddSanitizedLogTail(archive, DiagnosticLogService.CoreErrorLogPath, "logs/core-error-tail.log", 1200, sensitiveValues);
        AddSanitizedLogTail(archive, DiagnosticLogService.StartupLogPath, "logs/startup-tail.log", 400, sensitiveValues);
        AddLatestAccessLog(archive, sensitiveValues);
        return destinationPath;
    }

    private static string BuildReport(DiagnosticResult result, AppSettings settings, VmessProfile? profile)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Nexora 脱敏诊断报告");
        builder.AppendLine($"生成时间: {result.CompletedAt:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"应用版本: {AppVersionHelper.GetCurrentVersionName()}");
        builder.AppendLine($"操作系统: {Environment.OSVersion}");
        builder.AppendLine($"管理员权限: {TunService.IsAdministrator()}");
        builder.AppendLine($"结果: {result.Summary}");
        builder.AppendLine();
        foreach (var check in result.Checks)
        {
            builder.AppendLine($"[{(check.Success ? "通过" : "异常")}] {check.Name}: {Sanitize(check.Detail)}");
        }

        builder.AppendLine();
        builder.AppendLine($"系统代理模式: {settings.SystemProxyMode}");
        builder.AppendLine($"路由模式: {settings.RoutingMode}");
        builder.AppendLine($"普通 TUN: {settings.IsTunEnabled}");
        builder.AppendLine($"Codex 优化: {settings.OpenAiCodexOptimizationEnabled}");
        builder.AppendLine($"UWP 优化: {settings.UwpOptimizationEnabled}");
        builder.AppendLine($"活动节点: {(profile is null ? "无" : $"{profile.ProtocolDisplay} / {MaskHost(profile.Address)}:{profile.Port}")}");
        return builder.ToString();
    }

    private static string BuildRuntimeSummary(AppSettings settings, VmessProfile? profile) =>
        $"""
        HTTP port: {settings.HttpPort}
        SOCKS port: {settings.SocksPort}
        API port: {settings.ApiPort}
        Core: {settings.CoreExecutable}
        System proxy mode: {settings.SystemProxyMode}
        Routing mode: {settings.RoutingMode}
        TUN configured: {settings.IsTunEnabled}
        TUN running: {TunService.IsRunning}
        OpenAI optimization configured: {settings.OpenAiCodexOptimizationEnabled}
        UWP optimization configured: {settings.UwpOptimizationEnabled}
        OpenAI network layer running: {OpenAiNetworkLayerService.IsRunning}
        OpenAI network layer last start: {OpenAiNetworkLayerService.LastStartedAtUtc?.ToLocalTime():yyyy-MM-dd HH:mm:ss}
        Active protocol: {profile?.ProtocolDisplay ?? "none"}
        Active endpoint: {(profile is null ? "none" : $"{MaskHost(profile.Address)}:{profile.Port}")}
        """;

    private static void AddLatestAccessLog(ZipArchive archive, IReadOnlyCollection<string> sensitiveValues)
    {
        var directory = Path.GetDirectoryName(DiagnosticLogService.AppLogPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;
        var latest = Directory.EnumerateFiles(directory, "access-*.log")
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .FirstOrDefault();
        if (latest is not null)
        {
            AddSanitizedLogTail(archive, latest.FullName, "logs/access-tail.log", 1500, sensitiveValues);
        }
    }

    private static void AddSanitizedLogTail(
        ZipArchive archive,
        string sourcePath,
        string entryName,
        int maxLines,
        IReadOnlyCollection<string> sensitiveValues)
    {
        if (!File.Exists(sourcePath)) return;
        var lines = File.ReadLines(sourcePath).TakeLast(maxLines).Select(line => Sanitize(line, sensitiveValues));
        WriteEntry(archive, entryName, string.Join(Environment.NewLine, lines));
    }

    private static IReadOnlyCollection<string> BuildSensitiveValues(AppSettings settings)
    {
        var values = new HashSet<string>(StringComparer.Ordinal);
        foreach (var profile in settings.Profiles)
        {
            foreach (var value in new[] { profile.UserId, profile.Password, profile.RealityPublicKey, profile.RealityShortId })
            {
                if (!string.IsNullOrWhiteSpace(value) && value.Length >= 4)
                {
                    values.Add(value);
                }
            }
        }

        foreach (var source in settings.SubscriptionSources.Values)
        {
            if (!string.IsNullOrWhiteSpace(source.Url))
            {
                values.Add(source.Url);
            }
        }

        return values;
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Fastest);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(content);
    }

    private static string ResolveCorePath(string configuredPath) => CoreRunner.ResolveCorePath(configuredPath);

    private static string MaskHost(string host)
    {
        if (IPAddress.TryParse(host, out var address))
        {
            var parts = address.ToString().Split('.');
            return parts.Length == 4 ? $"{parts[0]}.{parts[1]}.*.*" : "[IPv6 hidden]";
        }

        var labels = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return labels.Length >= 2 ? $"*.{string.Join('.', labels.TakeLast(2))}" : "[host hidden]";
    }

    private static string Sanitize(string text) => Sanitize(text, []);

    private static string Sanitize(string text, IReadOnlyCollection<string> sensitiveValues)
    {
        var sanitized = text;
        foreach (var sensitiveValue in sensitiveValues)
        {
            sanitized = sanitized.Replace(sensitiveValue, "[REDACTED]", StringComparison.Ordinal);
        }

        sanitized = UuidRegex().Replace(sanitized, "[UUID]");
        sanitized = EmailRegex().Replace(sanitized, "[EMAIL]");
        sanitized = SecretRegex().Replace(sanitized, "$1=[REDACTED]");
        sanitized = BearerRegex().Replace(sanitized, "$1 [REDACTED]");
        sanitized = UrlCredentialRegex().Replace(sanitized, "$1[REDACTED]@");
        sanitized = Ipv4Regex().Replace(sanitized, match => MaskIpv4(match.Value));
        return sanitized;
    }

    private static string MaskIpv4(string value)
    {
        if (!IPAddress.TryParse(value, out var address)) return value;
        var bytes = address.GetAddressBytes();
        if (bytes.Length != 4 || bytes[0] == 127 || bytes[0] == 10 ||
            (bytes[0] == 192 && bytes[1] == 168) ||
            (bytes[0] == 172 && bytes[1] is >= 16 and <= 31))
        {
            return value;
        }

        return $"{bytes[0]}.{bytes[1]}.*.*";
    }

    [GeneratedRegex(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[1-5][0-9a-fA-F]{3}-[89abAB][0-9a-fA-F]{3}-[0-9a-fA-F]{12}\b")]
    private static partial Regex UuidRegex();

    [GeneratedRegex(@"\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b", RegexOptions.IgnoreCase)]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"(?i)\b(token|access_token|refresh_token|password|passwd|secret|api[_-]?key|authorization)\s*[=:]\s*[^\s,;&]+")]
    private static partial Regex SecretRegex();

    [GeneratedRegex(@"(?i)\b(Bearer|Basic)\s+[^\s]+")]
    private static partial Regex BearerRegex();

    [GeneratedRegex(@"(https?://)[^/@\s]+@", RegexOptions.IgnoreCase)]
    private static partial Regex UrlCredentialRegex();

    [GeneratedRegex(@"(?<![\d.])(?:\d{1,3}\.){3}\d{1,3}(?![\d.])")]
    private static partial Regex Ipv4Regex();
}
