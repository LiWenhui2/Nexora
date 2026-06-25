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

    public static async Task<SubscriptionImportResult> ImportAsync(string input, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            throw new InvalidOperationException("请输入节点链接、订阅地址或本地文本内容。");
        }

        var trimmed = input.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) &&
            uri.Scheme is "http" or "https" &&
            ShouldTreatAsSubscriptionInput(uri, trimmed))
        {
            return await ImportFromUrlAsync(uri, cancellationToken);
        }

        return new SubscriptionImportResult
        {
            Profiles = ParseProfiles(trimmed, "", DateTime.Now),
            SubscriptionName = ""
        };
    }

    public static SubscriptionImportResult ImportNodeLinks(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            throw new InvalidOperationException("请输入节点链接或本地文本内容。");
        }

        return new SubscriptionImportResult
        {
            Profiles = ParseProfiles(input.Trim(), "", DateTime.Now),
            SubscriptionName = ""
        };
    }

    private static async Task<SubscriptionImportResult> ImportFromUrlAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("Nexora/1.1.0");

        using var response = await HttpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var sourceName = string.IsNullOrWhiteSpace(uri.Host) ? "订阅" : uri.Host;
        return new SubscriptionImportResult
        {
            Profiles = ParseProfiles(content, sourceName, DateTime.Now),
            TrafficInfo = TryParseSubscriptionUserInfo(response),
            SourceUrl = uri.ToString(),
            SubscriptionName = sourceName
        };
    }

    internal static SubscriptionTrafficInfo? TryParseSubscriptionUserInfo(HttpResponseMessage response)
    {
        var raw = ReadSubscriptionUserInfoHeader(response);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        long upload = 0;
        long download = 0;
        long? total = null;
        long? expireUnix = null;

        foreach (var part in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = part.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = part[..separator].Trim();
            var value = part[(separator + 1)..].Trim();
            if (!long.TryParse(value, out var number))
            {
                continue;
            }

            switch (key.ToLowerInvariant())
            {
                case "upload":
                    upload = number;
                    break;
                case "download":
                    download = number;
                    break;
                case "total":
                    total = number;
                    break;
                case "expire":
                    expireUnix = number;
                    break;
            }
        }

        var expireAtUtc = ParseExpireUnix(expireUnix);
        if (total is null && upload == 0 && download == 0 && expireAtUtc is null)
        {
            return null;
        }

        return new SubscriptionTrafficInfo
        {
            UploadBytes = upload,
            DownloadBytes = download,
            TotalBytes = total,
            ExpireAtUtc = expireAtUtc
        };
    }

    private static string? ReadSubscriptionUserInfoHeader(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("subscription-userinfo", out var responseValues))
        {
            return responseValues.FirstOrDefault();
        }

        if (response.Content.Headers.TryGetValues("subscription-userinfo", out var contentValues))
        {
            return contentValues.FirstOrDefault();
        }

        return null;
    }

    private static DateTime? ParseExpireUnix(long? expireUnix)
    {
        if (expireUnix is not long unix || unix <= 0)
        {
            return null;
        }

        var seconds = unix > 9999999999 ? unix / 1000 : unix;
        var expireAtUtc = DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
        return expireAtUtc.Year >= 2099 ? new DateTime(2099, 12, 31, 23, 59, 59, DateTimeKind.Utc) : expireAtUtc;
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
            profile.UpdatedAt = updatedAt ?? DateTime.Now;
            profile.Region = NodeRegionHelper.Resolve(profile);
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

    private static bool ShouldTreatAsSubscriptionInput(Uri uri, string raw)
    {
        if (!string.IsNullOrWhiteSpace(uri.UserInfo))
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
