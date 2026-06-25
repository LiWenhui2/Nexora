using System.Diagnostics;
using System.Net;
using System.Net.Http;

namespace NaiwaProxy.Services;

public static class WebsiteConnectivityTestService
{
    public const int DefaultTimeoutMs = 15000;

    public static IReadOnlyList<WebsiteTestTarget> DefaultTargets { get; } =
    [
        new("GitHub", "https://github.com", "github.png"),
        new("ChatGPT", "https://chatgpt.com", "chatgpt.png"),
        new("YouTube", "https://www.youtube.com", "youtube.png"),
        new("Google", "https://www.google.com", "google.png"),
        new("Twitter", "https://x.com", "twitter.png"),
        new("Wikipedia", "https://www.wikipedia.org", "wikipedia.png"),
        new("Discord", "https://discord.com", "discord.png"),
        new("Telegram", "https://web.telegram.org", "telegram.png")
    ];

    public static async Task<WebsiteTestResult> TestAsync(
        string url,
        int httpProxyPort,
        int timeoutMs = DefaultTimeoutMs,
        CancellationToken cancellationToken = default)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeoutMs);

        using var handler = new HttpClientHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{httpProxyPort}"),
            UseProxy = true,
            AllowAutoRedirect = true
        };

        using var client = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Nexora/1.1.0");

        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeoutSource.Token);
            stopwatch.Stop();

            if ((int)response.StatusCode >= 500)
            {
                return new WebsiteTestResult(false, (int)stopwatch.ElapsedMilliseconds, $"HTTP {(int)response.StatusCode}");
            }

            return new WebsiteTestResult(true, (int)stopwatch.ElapsedMilliseconds, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return new WebsiteTestResult(false, null, "超时");
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            return new WebsiteTestResult(false, null, ShortenError(ex.Message));
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new WebsiteTestResult(false, null, ShortenError(ex.Message));
        }
    }

    private static string ShortenError(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "失败";
        }

        var firstLine = message.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? message;
        return firstLine.Length <= 48 ? firstLine : string.Concat(firstLine.AsSpan(0, 45), "…");
    }

    public sealed record WebsiteTestTarget(string Name, string Url, string IconFileName);

    public sealed record WebsiteTestResult(bool Success, int? LatencyMs, string? ErrorMessage);
}
