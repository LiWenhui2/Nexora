using NaiwaProxy.Models;

namespace NaiwaProxy.Services;

public static class SubscriptionTrafficHelper
{
    public static bool IsTrafficExhausted(SubscriptionTrafficInfo? trafficInfo)
    {
        if (trafficInfo is null)
        {
            return false;
        }

        if (trafficInfo.RemainingBytes is 0)
        {
            return true;
        }

        return trafficInfo.TotalBytes is long totalBytes && totalBytes > 0 &&
               trafficInfo.UploadBytes + trafficInfo.DownloadBytes >= totalBytes;
    }

    public static bool AreProfilesTrafficExhausted(IEnumerable<VmessProfile> profiles)
    {
        var list = profiles.ToList();
        if (list.Count == 0)
        {
            return false;
        }

        return list.Any(IsProfileTrafficExhausted);
    }

    public static bool IsProfileTrafficExhausted(VmessProfile profile)
    {
        if (profile.XpanelRemainingBytes is 0)
        {
            return true;
        }

        if (profile.XpanelTotalBytes is long total && total > 0)
        {
            if (profile.XpanelUsedBytes is long used && used >= total)
            {
                return true;
            }

            var subscriptionUsed = profile.SubscriptionUploadBytes + profile.SubscriptionDownloadBytes;
            if (subscriptionUsed >= total)
            {
                return true;
            }
        }

        if (profile.SubscriptionTotalBytes is long subscriptionTotal && subscriptionTotal > 0)
        {
            var subscriptionUsed = profile.SubscriptionUploadBytes + profile.SubscriptionDownloadBytes;
            if (subscriptionUsed >= subscriptionTotal)
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsLikelyTrafficExhaustedError(Exception exception) =>
        IsLikelyTrafficExhaustedErrorMessage(exception.Message);

    public static bool IsLikelyTrafficExhaustedErrorMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        return message.Contains("410", StringComparison.Ordinal) ||
               message.Contains("Gone", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("503", StringComparison.Ordinal) ||
               message.Contains("Service Unavailable", StringComparison.OrdinalIgnoreCase);
    }
}
