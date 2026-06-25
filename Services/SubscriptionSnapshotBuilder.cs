using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using NaiwaProxy.Models;

namespace NaiwaProxy.Services;

public static class SubscriptionSnapshotBuilder
{
    public static SubscriptionSnapshot Build(
        SubscriptionImportResult importResult,
        int subscriptionId,
        int userId,
        string subscriptionName,
        string? sourceUrl,
        DateTime? createdAtUtc = null)
    {
        var now = DateTime.UtcNow;
        var generatedAt = FormatTimestamp(now);
        var createdAt = FormatTimestamp(createdAtUtc ?? now);
        var traffic = importResult.TrafficInfo;
        var expireAt = FormatTimestamp(SubscriptionMetadataHelper.ResolveExpireAtUtc(importResult)) ?? "2099-12-31T23:59:59Z";
        var totalBytes = traffic?.TotalBytes ?? importResult.Profiles.FirstOrDefault()?.XpanelTotalBytes ?? 0L;
        var remainBytes = traffic?.RemainingBytes ?? importResult.Profiles.FirstOrDefault()?.XpanelRemainingBytes ?? totalBytes;

        var snapshot = new SubscriptionSnapshot
        {
            GeneratedAt = generatedAt,
            Subscriptions =
            [
                new SnapshotSubscription
                {
                    Id = subscriptionId,
                    UserId = userId,
                    Name = subscriptionName,
                    UrlCiphertext = "",
                    UrlHash = string.IsNullOrWhiteSpace(sourceUrl) ? "" : ComputeSha256(sourceUrl),
                    Enabled = 1,
                    TotalBytes = totalBytes,
                    RemainBytes = remainBytes,
                    ExpireAt = expireAt,
                    LastUpdateTime = generatedAt,
                    CreatedAt = createdAt,
                    UpdatedAt = generatedAt
                }
            ]
        };

        var nodeId = 1;
        foreach (var profile in importResult.Profiles)
        {
            var nodeNow = FormatTimestamp(now);
            var nodeExpire = FormatTimestamp(profile.XpanelExpiryTime) ?? expireAt;
            var shareLink = ShareLinkBuilder.Build(profile);
            var protocol = NormalizeProtocol(profile.Protocol);
            var security = ResolveSecurity(profile);
            var credentialUuid = ResolveCredentialUuid(profile);

            snapshot.ProxyNodes.Add(new SnapshotProxyNode
            {
                Id = nodeId++,
                UserId = userId,
                SubscriptionId = subscriptionId,
                Name = profile.DisplayName,
                OriginalName = profile.DisplayName,
                Remark = string.IsNullOrWhiteSpace(profile.Remark) ? profile.DisplayName : profile.Remark,
                Protocol = protocol,
                Address = profile.Address,
                Port = profile.Port,
                Transport = string.IsNullOrWhiteSpace(profile.Network) ? "tcp" : profile.Network,
                Security = security,
                Sni = profile.Sni,
                Host = profile.Host,
                Path = profile.Path,
                Alpn = "",
                CountryCode = "",
                Region = profile.Region,
                City = "",
                CredentialCiphertext = "",
                Credential = new SnapshotCredential
                {
                    AlterId = profile.AlterId,
                    Email = BuildCredentialEmail(credentialUuid),
                    Uuid = credentialUuid
                },
                ConfigJson = new SnapshotConfigJson
                {
                    ExpireAt = nodeExpire,
                    Network = string.IsNullOrWhiteSpace(profile.Network) ? "tcp" : profile.Network,
                    RemainBytes = profile.XpanelRemainingBytes ?? remainBytes,
                    TotalBytes = profile.XpanelTotalBytes ?? totalBytes,
                    UsedBytes = profile.XpanelUsedBytes ?? 0
                },
                ShareLinkCiphertext = "",
                ShareLink = shareLink,
                NodeHash = "",
                Enabled = 1,
                CreatedAt = nodeNow,
                UpdatedAt = nodeNow
            });
        }

        return snapshot;
    }

    public static string? ResolveSubscriptionExpireAt(SubscriptionImportResult importResult) =>
        FormatTimestamp(SubscriptionMetadataHelper.ResolveExpireAtUtc(importResult));

    public static string? ResolveSubscriptionExpireAt(IReadOnlyList<VmessProfile> profiles)
    {
        foreach (var profile in profiles)
        {
            if (profile.XpanelExpiryTime is DateTime expiry)
            {
                return FormatTimestamp(expiry);
            }
        }

        return null;
    }

    public static string FormatApiExpireAt(DateTime? expiryUtc)
    {
        var value = expiryUtc ?? new DateTime(2099, 12, 31, 23, 59, 59, DateTimeKind.Utc);
        return value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
    }

    private static string FormatTimestamp(DateTime? value)
    {
        var utc = (value ?? DateTime.UtcNow).ToUniversalTime();
        return utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
    }

    private static string NormalizeProtocol(string protocol) =>
        protocol.ToLowerInvariant() switch
        {
            "shadowsocks" => "ss",
            "socks5" => "socks",
            _ => protocol.ToLowerInvariant()
        };

    private static string ResolveSecurity(VmessProfile profile)
    {
        if (!string.IsNullOrWhiteSpace(profile.Tls))
        {
            return profile.Tls.Equals("tls", StringComparison.OrdinalIgnoreCase) ? "tls" : profile.Tls;
        }

        if (profile.Protocol.Equals("vmess", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(profile.Security) &&
            !profile.Security.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return profile.Security;
        }

        return "none";
    }

    private static string ResolveCredentialUuid(VmessProfile profile)
    {
        if (!string.IsNullOrWhiteSpace(profile.UserId))
        {
            return profile.UserId;
        }

        return profile.Password;
    }

    private static string BuildCredentialEmail(string uuid)
    {
        if (string.IsNullOrWhiteSpace(uuid))
        {
            return "";
        }

        var suffix = uuid.Replace("-", "", StringComparison.Ordinal)[..Math.Min(12, uuid.Replace("-", "", StringComparison.Ordinal).Length)];
        return $"client-{suffix}@xpanel.local";
    }

    private static string ComputeSha256(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
