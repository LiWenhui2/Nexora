using System.Runtime.InteropServices;
using NaiwaProxy.Models;

namespace NaiwaProxy.Services;

public static class OpenAiCodexOptimizationService
{
    private const int WmSettingChange = 0x001A;
    private const uint SmtoAbortIfHung = 0x0002;
    private static readonly string[] ProxyEnvironmentVariableNames =
    [
        "HTTP_PROXY",
        "HTTPS_PROXY",
        "ALL_PROXY",
        "NO_PROXY"
    ];

    public static readonly string[] ProxyDomains =
    [
        "openai.com",
        "chatgpt.com",
        "oaistatic.com",
        "oaiusercontent.com",
        "auth0.openai.com",
        "platform.openai.com",
        "api.openai.com",
        "cdn.openai.com",
        "ws.chatgpt.com",
        "desktop.chat.openai.com",
        "ab.chatgpt.com",
        "statsigapi.net",
        "featuregates.org",
        "sentry.io"
    ];

    private static readonly string[] LegacyProxyProcesses =
    [
        "codex.exe",
        "ChatGPT.exe",
        "Cursor.exe"
    ];

    public static readonly string[] PreWarmUrls =
    [
        "https://chatgpt.com",
        "https://api.openai.com",
        "https://ws.chatgpt.com"
    ];

    public static void Apply(AppSettings settings)
    {
        settings.OpenAiCodexOptimizationSnapshot = new OpenAiCodexOptimizationSnapshot
        {
            RoutingMode = settings.RoutingMode,
            SystemProxyMode = settings.SystemProxyMode,
            CustomRouting = CloneCustomRouting(settings.CustomRouting),
            ProxyEnvironmentVariables = CaptureProxyEnvironmentVariables()
        };

        settings.SystemProxyMode = "Auto";
        EnsureRulesMerged(settings);
        ApplyProxyEnvironment(settings);
        settings.OpenAiCodexOptimizationEnabled = true;
    }

    public static void Restore(AppSettings settings)
    {
        var snapshot = settings.OpenAiCodexOptimizationSnapshot;
        if (snapshot is null)
        {
            settings.OpenAiCodexOptimizationEnabled = false;
            return;
        }

        settings.RoutingMode = snapshot.RoutingMode;
        settings.SystemProxyMode = snapshot.SystemProxyMode;
        settings.CustomRouting = CloneCustomRouting(snapshot.CustomRouting);
        RestoreProxyEnvironmentVariables(snapshot.ProxyEnvironmentVariables);
        settings.OpenAiCodexOptimizationEnabled = false;
        settings.OpenAiCodexOptimizationSnapshot = null;
    }

    public static bool EnsureRulesMerged(AppSettings settings)
    {
        settings.CustomRouting ??= new CustomRoutingSettings();
        var domainsChanged = MergeDistinct(settings.CustomRouting.ProxyDomains, ProxyDomains);
        var processesChanged = RemoveDistinct(settings.CustomRouting.ProxyProcesses, LegacyProxyProcesses);
        return domainsChanged || processesChanged;
    }

    public static bool EnsureProxyEnvironment(AppSettings settings)
    {
        settings.OpenAiCodexOptimizationSnapshot ??= new OpenAiCodexOptimizationSnapshot
        {
            RoutingMode = settings.RoutingMode,
            SystemProxyMode = settings.SystemProxyMode,
            CustomRouting = CloneCustomRouting(settings.CustomRouting),
            ProxyEnvironmentVariables = CaptureProxyEnvironmentVariables()
        };

        if (settings.OpenAiCodexOptimizationSnapshot.ProxyEnvironmentVariables.Count == 0)
        {
            settings.OpenAiCodexOptimizationSnapshot.ProxyEnvironmentVariables = CaptureProxyEnvironmentVariables();
        }

        var expectedHttpProxy = $"http://127.0.0.1:{settings.HttpPort}";
        var expectedSocksProxy = $"socks5://127.0.0.1:{settings.SocksPort}";
        var changed =
            !string.Equals(Environment.GetEnvironmentVariable("HTTP_PROXY", EnvironmentVariableTarget.User), expectedHttpProxy, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Environment.GetEnvironmentVariable("HTTPS_PROXY", EnvironmentVariableTarget.User), expectedHttpProxy, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Environment.GetEnvironmentVariable("ALL_PROXY", EnvironmentVariableTarget.User), expectedSocksProxy, StringComparison.OrdinalIgnoreCase);

        if (changed)
        {
            ApplyProxyEnvironment(settings);
        }

        return changed;
    }

    public static async Task PreWarmAsync(int httpProxyPort, CancellationToken cancellationToken = default)
    {
        var tasks = PreWarmUrls.Select(url => PreWarmUrlAsync(url, httpProxyPort, cancellationToken));
        await Task.WhenAll(tasks);
    }

    public static async Task CompleteProxyReadinessAsync(
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        await CoreRunner.WaitForPortAsync(settings.HttpPort, 5000, cancellationToken);
        await CoreRunner.WaitForPortAsync(settings.SocksPort, 5000, cancellationToken);
        await SystemProxyService.PrepareOpenAiDesktopProxyAsync(
            settings.HttpPort,
            settings.UwpOptimizationEnabled,
            cancellationToken);
        await PreWarmAsync(settings.HttpPort, cancellationToken);

        // Re-announce the already-valid proxy after the OpenAI endpoints have
        // been verified. Packaged Chromium apps may otherwise retain the
        // failed proxy state they observed while Windows was signing in.
        SystemProxyService.RefreshHttpProxy(settings.HttpPort, settings.UwpOptimizationEnabled);
        DiagnosticLogService.Info("ChatGPT/Codex proxy readiness completed and proxy consumers were refreshed.");
    }

    private static async Task PreWarmUrlAsync(
        string url,
        int httpProxyPort,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var result = await WebsiteConnectivityTestService.TestAsync(
                url,
                httpProxyPort,
                timeoutMs: 8000,
                cancellationToken: cancellationToken);
            DiagnosticLogService.Info(
                result.Success
                    ? $"OpenAI/Codex prewarm succeeded for {url} ({result.LatencyMs} ms)."
                    : $"OpenAI/Codex prewarm failed for {url}: {result.ErrorMessage ?? "unknown error"}.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Warning($"OpenAI/Codex prewarm error for {url}: {ex.Message}");
        }
    }

    private static bool MergeDistinct(List<string> target, IEnumerable<string> values)
    {
        var changed = false;
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (target.Any(existing => string.Equals(existing, value, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            target.Add(value);
            changed = true;
        }

        return changed;
    }

    private static bool RemoveDistinct(List<string> target, IEnumerable<string> values)
    {
        var changed = false;
        foreach (var value in values)
        {
            changed |= target.RemoveAll(existing =>
                string.Equals(existing, value, StringComparison.OrdinalIgnoreCase)) > 0;
        }

        return changed;
    }

    private static Dictionary<string, string?> CaptureProxyEnvironmentVariables()
    {
        return ProxyEnvironmentVariableNames.ToDictionary(
            name => name,
            name => Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User),
            StringComparer.OrdinalIgnoreCase);
    }

    private static void ApplyProxyEnvironment(AppSettings settings)
    {
        var httpProxy = $"http://127.0.0.1:{settings.HttpPort}";
        Environment.SetEnvironmentVariable("HTTP_PROXY", httpProxy, EnvironmentVariableTarget.User);
        Environment.SetEnvironmentVariable("HTTPS_PROXY", httpProxy, EnvironmentVariableTarget.User);
        Environment.SetEnvironmentVariable("ALL_PROXY", $"socks5://127.0.0.1:{settings.SocksPort}", EnvironmentVariableTarget.User);

        var existingNoProxy = Environment.GetEnvironmentVariable("NO_PROXY", EnvironmentVariableTarget.User);
        var noProxyEntries = (existingNoProxy ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        MergeDistinct(noProxyEntries, ["localhost", "127.0.0.1", "::1"]);
        Environment.SetEnvironmentVariable("NO_PROXY", string.Join(',', noProxyEntries), EnvironmentVariableTarget.User);
        BroadcastEnvironmentChanged();
    }

    private static void RestoreProxyEnvironmentVariables(IReadOnlyDictionary<string, string?> snapshot)
    {
        foreach (var name in ProxyEnvironmentVariableNames)
        {
            snapshot.TryGetValue(name, out var value);
            Environment.SetEnvironmentVariable(name, value, EnvironmentVariableTarget.User);
        }

        BroadcastEnvironmentChanged();
    }

    private static void BroadcastEnvironmentChanged()
    {
        SendMessageTimeout(
            new IntPtr(0xffff),
            WmSettingChange,
            IntPtr.Zero,
            "Environment",
            SmtoAbortIfHung,
            3000,
            out _);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd,
        int msg,
        IntPtr wParam,
        string lParam,
        uint flags,
        uint timeout,
        out IntPtr result);

    private static CustomRoutingSettings CloneCustomRouting(CustomRoutingSettings routing)
    {
        routing ??= new CustomRoutingSettings();
        return new CustomRoutingSettings
        {
            ProxyDomains = [.. routing.ProxyDomains],
            DirectDomains = [.. routing.DirectDomains],
            BypassChinaDomains = [.. routing.BypassChinaDomains],
            BlockDomains = [.. routing.BlockDomains],
            ProxyIps = [.. routing.ProxyIps],
            DirectIps = [.. routing.DirectIps],
            BypassChinaIps = [.. routing.BypassChinaIps],
            BlockIps = [.. routing.BlockIps],
            ProxyProcesses = [.. routing.ProxyProcesses],
            DirectProcesses = [.. routing.DirectProcesses],
            BypassChinaProcesses = [.. routing.BypassChinaProcesses],
            BlockProcesses = [.. routing.BlockProcesses]
        };
    }
}
