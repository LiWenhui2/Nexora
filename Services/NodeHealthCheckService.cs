using System.Diagnostics;
using System.Net;
using System.Net.Http;
using NaiwaProxy.Models;

namespace NaiwaProxy.Services;

public sealed record NodeHealthResult(
    bool TcpConnectSuccess,
    bool ProxyHandshakeSuccess,
    bool WebsiteAccessSuccess,
    int StabilityPercent,
    double? DownloadSpeedMbps,
    int? LatencyMs)
{
    public bool Success => TcpConnectSuccess && ProxyHandshakeSuccess && WebsiteAccessSuccess;
}

public static class NodeHealthCheckService
{
    public static async Task<NodeHealthResult> CheckAsync(
        AppSettings settings,
        VmessProfile profile,
        bool isActive,
        CancellationToken token = default)
    {
        var tcpSuccesses = 0;
        for (var index = 0; index < 3; index++)
        {
            if (await LatencyTestService.MeasureTcpAsync(profile.Address, profile.Port, 4000, token) is not null) tcpSuccesses++;
        }

        var proxyLatency = isActive
            ? await ProbeActiveProxyAsync(settings.HttpPort, token)
            : await NodeLatencyTestService.MeasureAsync(settings, profile, token);
        var proxySuccess = proxyLatency is not null;
        var websiteSuccesses = proxySuccess ? 1 : 0;
        double? speed = null;
        if (isActive && proxySuccess)
        {
            websiteSuccesses = await MeasureActiveStabilityAsync(settings.HttpPort, token);
            speed = await MeasureDownloadSpeedAsync(settings.HttpPort, token);
        }

        var stabilitySamples = isActive ? 3 : 1;
        var stability = (int)Math.Round(websiteSuccesses * 100d / stabilitySamples);
        return new NodeHealthResult(
            tcpSuccesses > 0,
            proxySuccess,
            websiteSuccesses > 0,
            stability,
            speed,
            proxyLatency);
    }

    private static async Task<int?> ProbeActiveProxyAsync(int port, CancellationToken token)
    {
        var result = await WebsiteConnectivityTestService.TestAsync(
            "http://cp.cloudflare.com/generate_204", port, 8000, token);
        return result.Success ? result.LatencyMs : null;
    }

    private static async Task<int> MeasureActiveStabilityAsync(int port, CancellationToken token)
    {
        var successes = 0;
        for (var index = 0; index < 3; index++)
        {
            if (await ProbeActiveProxyAsync(port, token) is not null) successes++;
        }
        return successes;
    }

    private static async Task<double?> MeasureDownloadSpeedAsync(int port, CancellationToken token)
    {
        const int bytes = 1_000_000;
        using var handler = new HttpClientHandler { Proxy = new WebProxy($"http://127.0.0.1:{port}"), UseProxy = true };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var data = await client.GetByteArrayAsync($"https://speed.cloudflare.com/__down?bytes={bytes}", token);
            stopwatch.Stop();
            if (data.Length == 0 || stopwatch.Elapsed.TotalSeconds <= 0) return null;
            return data.Length * 8d / stopwatch.Elapsed.TotalSeconds / 1_000_000d;
        }
        catch { return null; }
    }
}
