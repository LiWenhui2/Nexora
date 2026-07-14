using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using NaiwaProxy.Models;

namespace NaiwaProxy.Services;

public static class NodeLatencyTestService
{
    private const string ProbeUrl = "http://cp.cloudflare.com/generate_204";
    private const int StartupTimeoutMs = 8000;
    private const int ProbeTimeoutMs = 10000;
    private static readonly object PortReservationLock = new();
    private static readonly HashSet<int> ReservedPorts = [];

    public static async Task<int?> MeasureAsync(
        AppSettings sourceSettings,
        VmessProfile profile,
        CancellationToken cancellationToken = default)
    {
        if (profile.IsExpired)
        {
            return null;
        }

        var ports = ReservePorts(3);
        var testDirectory = Path.Combine(Path.GetTempPath(), "Nexora", "latency", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDirectory);
        var configPath = Path.Combine(testDirectory, "config.json");
        var accessLogPath = Path.Combine(testDirectory, "access.log");
        var errorLogPath = Path.Combine(testDirectory, "error.log");
        Process? process = null;
        try
        {
            var testSettings = new AppSettings
            {
                SocksPort = ports[0],
                HttpPort = ports[1],
                ApiPort = ports[2],
                CoreExecutable = sourceSettings.CoreExecutable,
                RoutingMode = "Global",
                AllowLanAccess = false,
                OpenAiCodexOptimizationEnabled = false,
                UwpOptimizationEnabled = false
            };
            await File.WriteAllTextAsync(
                configPath,
                CoreConfigBuilder.Build(testSettings, profile, accessLogPath, errorLogPath),
                cancellationToken);

            process = CoreRunner.Start(testSettings.CoreExecutable, configPath);
            await CoreRunner.WaitForPortAsync(testSettings.HttpPort, StartupTimeoutMs, cancellationToken);
            if (process.HasExited)
            {
                return null;
            }

            return await MeasureStableProxyLatencyAsync(
                testSettings.HttpPort,
                profile,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Warning(
                $"End-to-end latency test failed for {profile.DisplayName}: {ex.Message}");
            return null;
        }
        finally
        {
            CoreRunner.Stop(process);
            ReleaseReservedPorts(ports);
            try
            {
                Directory.Delete(testDirectory, recursive: true);
            }
            catch
            {
                // Temporary diagnostics are removed on a best-effort basis.
            }
        }
    }

    private static async Task<int?> MeasureStableProxyLatencyAsync(
        int httpProxyPort,
        VmessProfile profile,
        CancellationToken cancellationToken)
    {
        // The first request warms the temporary core and DNS cache. Formal samples
        // still create a fresh proxy connection, but use a lightweight HTTP 204
        // endpoint so remote TLS setup is not incorrectly counted as node latency.
        if (await MeasureSingleRequestAsync(httpProxyPort, cancellationToken) is null)
        {
            return null;
        }

        var samples = new List<int>(2);
        for (var index = 0; index < 2; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sample = await MeasureSingleRequestAsync(httpProxyPort, cancellationToken);
            if (sample is int latency)
            {
                samples.Add(latency);
            }

            if (index < 1)
            {
                await Task.Delay(120, cancellationToken);
            }
        }

        if (samples.Count == 0)
        {
            return null;
        }

        var rawStableLatency = samples.Min();
        var roundTripFactor = EstimateRoundTripFactor(profile);
        var normalizedLatency = Math.Max(1, (int)Math.Round(
            rawStableLatency / (double)roundTripFactor,
            MidpointRounding.AwayFromZero));
        DiagnosticLogService.Info(
            $"Node latency normalized for {profile.DisplayName}: raw={rawStableLatency} ms, " +
            $"roundTrips={roundTripFactor}, result={normalizedLatency} ms.");
        return normalizedLatency;
    }

    private static int EstimateRoundTripFactor(VmessProfile profile)
    {
        // A fresh plain proxy request contains one RTT to establish the node
        // connection and one RTT for the request/response. TLS/Reality and
        // HTTP-based transports add their own handshake RTTs.
        var factor = 2;
        if (profile.Tls.Equals("tls", StringComparison.OrdinalIgnoreCase) ||
            profile.Tls.Equals("reality", StringComparison.OrdinalIgnoreCase))
        {
            factor++;
        }

        if (profile.Network.Equals("ws", StringComparison.OrdinalIgnoreCase) ||
            profile.Network.Equals("grpc", StringComparison.OrdinalIgnoreCase) ||
            profile.Network.Equals("httpupgrade", StringComparison.OrdinalIgnoreCase) ||
            profile.Network.Equals("h2", StringComparison.OrdinalIgnoreCase))
        {
            factor++;
        }

        return factor;
    }

    private static async Task<int?> MeasureSingleRequestAsync(
        int httpProxyPort,
        CancellationToken cancellationToken)
    {
        using var handler = new HttpClientHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{httpProxyPort}"),
            UseProxy = true,
            AllowAutoRedirect = false
        };
        using var client = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Nexora-Latency/1.1");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeoutMs);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"{ProbeUrl}?n={Guid.NewGuid():N}");
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
            stopwatch.Stop();
            return response.IsSuccessStatusCode ? (int)stopwatch.ElapsedMilliseconds : null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static int[] ReservePorts(int count)
    {
        lock (PortReservationLock)
        {
            var ports = new List<int>(count);
            while (ports.Count < count)
            {
                var port = CoreRunner.GetFreePort();
                if (ReservedPorts.Add(port))
                {
                    ports.Add(port);
                }
            }

            return [.. ports];
        }
    }

    private static void ReleaseReservedPorts(IEnumerable<int> ports)
    {
        lock (PortReservationLock)
        {
            foreach (var port in ports)
            {
                ReservedPorts.Remove(port);
            }
        }
    }
}
