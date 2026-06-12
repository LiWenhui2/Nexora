using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
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
            throw new InvalidOperationException("请输入节点链接、订阅地址或本地文本内容。");
        }

        var trimmed = input.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) &&
            uri.Scheme is "http" or "https" &&
            ShouldTreatAsSubscription(uri, trimmed))
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
            throw new FormatException("没有识别到支持的节点链接。");
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
        var pattern = @"(?i)\b(?:vmess|vless|trojan|ss|socks5?|https?)://[^\s""'<>]+";
        foreach (Match match in Regex.Matches(content, pattern))
        {
            var value = match.Value.Trim().TrimEnd(',', ';');
            if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || ShouldTreatAsSubscription(uri, value))
                {
                    continue;
                }
            }

            yield return value;
        }
    }

    private static bool ShouldTreatAsSubscription(Uri uri, string raw)
    {
        if (!string.IsNullOrWhiteSpace(uri.UserInfo))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(uri.Fragment))
        {
            return false;
        }

        if (!uri.IsDefaultPort)
        {
            return false;
        }

        return raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Length == 1;
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
