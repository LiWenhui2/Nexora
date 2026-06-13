using System.IO;
using System.Text.RegularExpressions;

namespace NaiwaProxy.Services;

public sealed partial class CoreAccessLogService
{
    private CancellationTokenSource? _tailCancellation;
    private long _readPosition;

    [GeneratedRegex(
        @"^(?<timestamp>\d{4}/\d{2}/\d{2}\s+\d{2}:\d{2}:\d{2}(?:\.\d+)?)\s+from\s+\S+\s+(?<status>accepted|rejected)\s+(?:(?<protocol>tcp|udp):(?<host>[^:\s]+):(?<port>\d+)|//(?<host2>[^:/\s]+):(?<port2>\d+))\s+\[(?<inbound>[^\s\]]+)\s*(?:->|>>)\s*(?<outbound>\w+)\]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AccessLineRegex();

    public void Start(string accessLogPath)
    {
        Stop();
        Directory.CreateDirectory(Path.GetDirectoryName(accessLogPath)!);
        if (!File.Exists(accessLogPath))
        {
            File.WriteAllText(accessLogPath, string.Empty);
        }

        _readPosition = new FileInfo(accessLogPath).Length;
        _tailCancellation = new CancellationTokenSource();
        _ = TailAsync(accessLogPath, _tailCancellation.Token);
    }

    public void Stop()
    {
        _tailCancellation?.Cancel();
        _tailCancellation?.Dispose();
        _tailCancellation = null;
        _readPosition = 0;
    }

    private async Task TailAsync(string accessLogPath, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (File.Exists(accessLogPath))
                {
                    await using var stream = new FileStream(
                        accessLogPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite);
                    if (_readPosition > stream.Length)
                    {
                        _readPosition = 0;
                    }

                    stream.Seek(_readPosition, SeekOrigin.Begin);
                    using var reader = new StreamReader(stream);
                    while (true)
                    {
                        var line = await reader.ReadLineAsync(cancellationToken);
                        if (line is null)
                        {
                            break;
                        }

                        ProcessLine(line);
                    }

                    _readPosition = stream.Position;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Ignore transient file read issues while core is rotating logs.
            }

            try
            {
                await Task.Delay(400, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private static void ProcessLine(string line)
    {
        var match = AccessLineRegex().Match(line);
        if (!match.Success)
        {
            return;
        }

        if (!match.Groups["status"].Value.Equals("accepted", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var timestamp = match.Groups["timestamp"].Value;
        var protocol = match.Groups["protocol"].Success
            ? match.Groups["protocol"].Value.ToLowerInvariant()
            : "tcp";
        var host = match.Groups["host"].Success
            ? match.Groups["host"].Value
            : match.Groups["host2"].Value;
        var port = match.Groups["port"].Success
            ? match.Groups["port"].Value
            : match.Groups["port2"].Value;
        var inbound = match.Groups["inbound"].Value;
        var outbound = match.Groups["outbound"].Value.ToLowerInvariant();

        if (ShouldSkipTraffic(host, outbound))
        {
            return;
        }

        var message = $"{timestamp} accepted {protocol}:{host}:{port} [{inbound} -> {outbound}]";
        DiagnosticLogService.Traffic(message);
    }

    private static bool ShouldSkipTraffic(string host, string outbound)
    {
        if (IsLoopbackHost(host))
        {
            return true;
        }

        return outbound.Equals("api", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLoopbackHost(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return host is "127.0.0.1" or "::1" or "0.0.0.0";
    }
}
