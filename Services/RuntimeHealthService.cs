using System.Net.NetworkInformation;
using System.Net.Sockets;
using NaiwaProxy.Models;

namespace NaiwaProxy.Services;

public sealed record RuntimeHealthSnapshot(
    DateTime CheckedAt,
    bool ProxyExpected,
    bool NetworkAvailable,
    bool CoreRunning,
    bool HttpPortReady,
    bool SocksPortReady,
    bool ApiPortReady,
    bool SystemProxyConsistent,
    bool TunExpected,
    bool TunRunning,
    bool OpenAiLayerExpected,
    bool OpenAiLayerRunning)
{
    public bool PortsReady => HttpPortReady && SocksPortReady && ApiPortReady;

    public bool IsHealthy => !ProxyExpected ||
        (NetworkAvailable &&
         CoreRunning &&
         PortsReady &&
         SystemProxyConsistent &&
         (!TunExpected || TunRunning) &&
         (!OpenAiLayerExpected || OpenAiLayerRunning));

    public string Summary
    {
        get
        {
            var failures = new List<string>();
            if (!NetworkAvailable) failures.Add("系统网络不可用");
            if (!CoreRunning) failures.Add("代理核心未运行");
            if (!HttpPortReady) failures.Add("HTTP 端口不可用");
            if (!SocksPortReady) failures.Add("SOCKS 端口不可用");
            if (!ApiPortReady) failures.Add("API 端口不可用");
            if (!SystemProxyConsistent) failures.Add("系统代理状态不一致");
            if (TunExpected && !TunRunning) failures.Add("TUN 未运行");
            if (OpenAiLayerExpected && !OpenAiLayerRunning) failures.Add("Codex 专用网络层未运行");
            return failures.Count == 0 ? "运行正常" : string.Join("；", failures);
        }
    }
}

public static class RuntimeHealthService
{
    public static async Task<RuntimeHealthSnapshot> CheckAsync(
        AppSettings settings,
        bool coreRunning,
        bool proxyDesiredRunning,
        CancellationToken cancellationToken = default)
    {
        var httpTask = IsPortReadyAsync(settings.HttpPort, cancellationToken);
        var socksTask = IsPortReadyAsync(settings.SocksPort, cancellationToken);
        var apiTask = IsPortReadyAsync(settings.ApiPort, cancellationToken);

        await Task.WhenAll(httpTask, socksTask, apiTask);

        var systemProxyConsistent = !proxyDesiredRunning || settings.SystemProxyMode switch
        {
            "Auto" => SystemProxyService.IsHttpProxyEnabled(settings.HttpPort),
            "Pac" => SystemProxyService.IsPacProxyEnabled(),
            _ => true
        };
        var tunExpected = proxyDesiredRunning && settings.IsTunEnabled;
        var openAiExpected = proxyDesiredRunning &&
                             settings.OpenAiCodexOptimizationEnabled &&
                             !settings.IsTunEnabled;

        return new RuntimeHealthSnapshot(
            DateTime.Now,
            proxyDesiredRunning,
            NetworkInterface.GetIsNetworkAvailable(),
            coreRunning,
            await httpTask,
            await socksTask,
            await apiTask,
            systemProxyConsistent,
            tunExpected,
            TunService.IsRunning,
            openAiExpected,
            OpenAiNetworkLayerService.IsRunning);
    }

    private static async Task<bool> IsPortReadyAsync(int port, CancellationToken cancellationToken)
    {
        if (port is <= 0 or > 65535)
        {
            return false;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(1200));
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", port, timeout.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
