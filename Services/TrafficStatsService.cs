using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using NaiwaProxy.Models;

namespace NaiwaProxy.Services;

public static partial class TrafficStatsService
{
    public static async Task<TrafficSnapshot> QueryAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        var corePath = CoreRunner.ResolveCorePath(settings.CoreExecutable);
        var startInfo = new ProcessStartInfo
        {
            FileName = corePath,
            Arguments = $"api statsquery --server=127.0.0.1:{settings.ApiPort} -pattern \"outbound>>>proxy>>>traffic>>>\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动 Xray API 查询。");

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "Xray API 查询失败。" : error.Trim());
        }

        return Parse(output);
    }

    private static TrafficSnapshot Parse(string output)
    {
        var jsonSnapshot = TryParseJson(output);
        if (jsonSnapshot is not null)
        {
            return jsonSnapshot;
        }

        long uplink = 0;
        long downlink = 0;

        foreach (Match match in StatRegex().Matches(output))
        {
            var name = match.Groups["name"].Value;
            if (!long.TryParse(match.Groups["value"].Value, out var value))
            {
                continue;
            }

            if (name.Contains("uplink", StringComparison.OrdinalIgnoreCase))
            {
                uplink += value;
            }
            else if (name.Contains("downlink", StringComparison.OrdinalIgnoreCase))
            {
                downlink += value;
            }
        }

        return new TrafficSnapshot(uplink, downlink);
    }

    private static TrafficSnapshot? TryParseJson(string output)
    {
        try
        {
            using var document = JsonDocument.Parse(output);
            if (!document.RootElement.TryGetProperty("stat", out var stats) &&
                !document.RootElement.TryGetProperty("stats", out stats))
            {
                return null;
            }

            long uplink = 0;
            long downlink = 0;
            foreach (var stat in stats.EnumerateArray())
            {
                if (!stat.TryGetProperty("name", out var nameElement) ||
                    !stat.TryGetProperty("value", out var valueElement))
                {
                    continue;
                }

                var name = nameElement.GetString() ?? "";
                var value = valueElement.ValueKind == JsonValueKind.String
                    ? long.TryParse(valueElement.GetString(), out var parsed) ? parsed : 0
                    : valueElement.GetInt64();

                if (name.Contains("uplink", StringComparison.OrdinalIgnoreCase))
                {
                    uplink += value;
                }
                else if (name.Contains("downlink", StringComparison.OrdinalIgnoreCase))
                {
                    downlink += value;
                }
            }

            return new TrafficSnapshot(uplink, downlink);
        }
        catch
        {
            return null;
        }
    }

    [GeneratedRegex(@"name:\s*""(?<name>[^""]+)""\s*value:\s*(?<value>\d+)|(?<name>outbound>>>proxy>>>traffic>>>\w+)\s+value:\s*(?<value>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex StatRegex();
}
