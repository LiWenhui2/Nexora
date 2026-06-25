using NaiwaProxy.Models;

namespace NaiwaProxy.Services;

public static class SubscriptionMetadataHelper
{
    public static void ApplyToProfiles(SubscriptionImportResult result, string subscriptionName)
    {
        foreach (var profile in result.Profiles)
        {
            profile.SubscriptionName = subscriptionName;

            if (result.TrafficInfo is null)
            {
                continue;
            }

            profile.SubscriptionUploadBytes = result.TrafficInfo.UploadBytes;
            profile.SubscriptionDownloadBytes = result.TrafficInfo.DownloadBytes;
            profile.SubscriptionTotalBytes = result.TrafficInfo.TotalBytes;

            if (result.TrafficInfo.ExpireAtUtc is DateTime expireUtc)
            {
                profile.XpanelExpiryTime ??= expireUtc;
            }

            if (result.TrafficInfo.TotalBytes is long totalBytes)
            {
                profile.XpanelTotalBytes ??= totalBytes;
            }

            var usedBytes = result.TrafficInfo.UploadBytes + result.TrafficInfo.DownloadBytes;
            if (usedBytes > 0)
            {
                profile.XpanelUsedBytes ??= usedBytes;
            }

            if (result.TrafficInfo.RemainingBytes is long remainingBytes)
            {
                profile.XpanelRemainingBytes ??= remainingBytes;
            }
        }
    }

    public static DateTime? ResolveExpireAtUtc(SubscriptionImportResult importResult)
    {
        if (importResult.TrafficInfo?.ExpireAtUtc is DateTime trafficExpire)
        {
            return trafficExpire;
        }

        return importResult.Profiles
            .Select(profile => profile.XpanelExpiryTime)
            .FirstOrDefault(expire => expire is not null);
    }
}
