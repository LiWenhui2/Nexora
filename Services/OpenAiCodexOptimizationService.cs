using NaiwaProxy.Models;

namespace NaiwaProxy.Services;

public static class OpenAiCodexOptimizationService
{
    public static readonly string[] ProxyDomains =
    [
        "openai.com",
        "chatgpt.com",
        "oaistatic.com",
        "oaiusercontent.com",
        "auth0.openai.com",
        "platform.openai.com",
        "api.openai.com",
        "cdn.openai.com"
    ];

    public static readonly string[] ProxyProcesses =
    [
        "codex.exe"
    ];

    public static readonly string[] PreWarmUrls =
    [
        "https://chatgpt.com",
        "https://api.openai.com"
    ];

    public static void Apply(AppSettings settings)
    {
        settings.OpenAiCodexOptimizationSnapshot = new OpenAiCodexOptimizationSnapshot
        {
            RoutingMode = settings.RoutingMode,
            SystemProxyMode = settings.SystemProxyMode,
            CustomRouting = CloneCustomRouting(settings.CustomRouting)
        };

        settings.RoutingMode = "Global";
        settings.SystemProxyMode = "Auto";
        EnsureRulesMerged(settings);
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
        settings.OpenAiCodexOptimizationEnabled = false;
        settings.OpenAiCodexOptimizationSnapshot = null;
    }

    public static bool EnsureRulesMerged(AppSettings settings)
    {
        settings.CustomRouting ??= new CustomRoutingSettings();
        var domainsChanged = MergeDistinct(settings.CustomRouting.ProxyDomains, ProxyDomains);
        var processesChanged = MergeDistinct(settings.CustomRouting.ProxyProcesses, ProxyProcesses);
        return domainsChanged || processesChanged;
    }

    public static async Task PreWarmAsync(int httpProxyPort, CancellationToken cancellationToken = default)
    {
        foreach (var url in PreWarmUrls)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var result = await WebsiteConnectivityTestService.TestAsync(url, httpProxyPort, cancellationToken: cancellationToken);
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
