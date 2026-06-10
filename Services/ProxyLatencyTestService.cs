using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using NaiwaProxy.Models;

namespace NaiwaProxy.Services;

public static class ProxyLatencyTestService
{
    private const string TestUrl = "http://www.gstatic.com/generate_204";
    public const int StartupTimeoutMs = 10000;
    public const int RequestTimeoutMs = 12000;

    public static async Task<int?> MeasureRealAsync(
        AppSettings settings,
        VmessProfile profile,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(profile.Address) || profile.Port is <= 0 or > 65535)
        {
            return null;
        }

        if (!Guid.TryParse(profile.UserId, out _))
        {
            return null;
        }

        var httpPort = CoreRunner.GetFreePort();
        var socksPort = CoreRunner.GetFreePort();
        while (socksPort == httpPort)
        {
            socksPort = CoreRunner.GetFreePort();
        }

        var testSettings = new AppSettings
        {
            HttpPort = httpPort,
            SocksPort = socksPort,
            CoreExecutable = settings.CoreExecutable
        };

        var configDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "NaiwaProxy",
            "latency-tests");
        Directory.CreateDirectory(configDirectory);

        var configPath = Path.Combine(configDirectory, $"{profile.Id}-{Guid.NewGuid():N}.json");
        Process? process = null;

        try
        {
            await File.WriteAllTextAsync(
                configPath,
                CoreConfigBuilder.Build(testSettings, profile),
                cancellationToken);

            process = CoreRunner.Start(settings.CoreExecutable, configPath);
            await CoreRunner.WaitForPortAsync(httpPort, StartupTimeoutMs, cancellationToken);
            return await MeasureHttpProxyLatencyAsync(httpPort, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
        finally
        {
            CoreRunner.Stop(process);
            TryDelete(configPath);
        }
    }

    private static async Task<int?> MeasureHttpProxyLatencyAsync(int httpPort, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(RequestTimeoutMs);

        using var handler = new SocketsHttpHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{httpPort}"),
            UseProxy = true
        };
        using var client = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        var stopwatch = Stopwatch.StartNew();
        using var response = await client.GetAsync(TestUrl, timeoutSource.Token);
        stopwatch.Stop();

        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NoContent)
        {
            return null;
        }

        return (int)stopwatch.ElapsedMilliseconds;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Ignore cleanup failures.
        }
    }
}
