using System.Diagnostics;
using System.Net.Sockets;

namespace NaiwaProxy.Services;

public static class LatencyTestService
{
    public const int DefaultTimeoutMs = 5000;

    public static async Task<int?> MeasureTcpAsync(string address, int port, int timeoutMs = DefaultTimeoutMs, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(address) || port is <= 0 or > 65535)
        {
            return null;
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeoutMs);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(address.Trim(), port, timeoutSource.Token);
            stopwatch.Stop();
            return (int)stopwatch.ElapsedMilliseconds;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch
        {
            return null;
        }
    }
}
