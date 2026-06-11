using System.Net.Http;
using System.Text;
using NaiwaProxy.Models;

namespace NaiwaProxy.Services;

public static class SubscriptionImportService
{
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    public static async Task<List<VmessProfile>> ImportAsync(string input, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            throw new InvalidOperationException("请输入 vmess:// 链接或订阅地址。");
        }

        var trimmed = input.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            return await ImportFromUrlAsync(uri, cancellationToken);
        }

        return ParseProfiles(trimmed, "", null);
    }

    private static async Task<List<VmessProfile>> ImportFromUrlAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("NaiwaProxy/0.1");

        using var response = await HttpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var sourceName = string.IsNullOrWhiteSpace(uri.Host) ? "订阅" : uri.Host;
        return ParseProfiles(content, sourceName, DateTime.Now);
    }

    private static List<VmessProfile> ParseProfiles(string content, string sourceName, DateTime? updatedAt)
    {
        var candidates = ExtractCandidates(content).ToList();
        if (candidates.Count == 0 && TryDecodeBase64(content, out var decoded))
        {
            candidates = ExtractCandidates(decoded).ToList();
        }

        if (candidates.Count == 0)
        {
            throw new FormatException("没有识别到 vmess:// 节点。");
        }

        var profiles = new List<VmessProfile>();
        foreach (var candidate in candidates)
        {
            var profile = VmessLinkParser.Parse(candidate);
            profile.SubscriptionName = sourceName;
            profile.SubscriptionUpdatedAt = updatedAt;
            profiles.Add(profile);
        }

        return profiles;
    }

    private static IEnumerable<string> ExtractCandidates(string content)
    {
        return content
            .Split(['\r', '\n', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => item.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryDecodeBase64(string content, out string decoded)
    {
        decoded = "";
        var normalized = content.Trim().Replace("\r", "").Replace("\n", "").Replace('-', '+').Replace('_', '/');
        if (normalized.Length == 0)
        {
            return false;
        }

        var padding = normalized.Length % 4;
        if (padding != 0)
        {
            normalized = normalized.PadRight(normalized.Length + 4 - padding, '=');
        }

        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(normalized));
            return true;
        }
        catch
        {
            return false;
        }
    }
}
