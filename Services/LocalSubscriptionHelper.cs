using NaiwaProxy.Models;

namespace NaiwaProxy.Services;

public readonly record struct SubscriptionGroupIdentity(string Name, bool IsLocal, bool IsManual)
{
    public static SubscriptionGroupIdentity LocalManual { get; } = new("", IsLocal: true, IsManual: true);

    public string SourceKey => IsManual
        ? LocalSubscriptionHelper.LocalLabel
        : IsLocal
            ? LocalSubscriptionHelper.GetLocalSourceKey(Name)
            : Name;
}

public static class LocalSubscriptionHelper
{
    public const string LocalLabel = "本地";
    public const string LocalPrefix = "本地 · ";
    public const string CloudPrefix = "云端 · ";
    private const string LocalSourcePrefix = "local:";

    public static string FormatLocalSubscriptionDisplay(string subscriptionName) =>
        $"{LocalPrefix}{subscriptionName}";

    public static string FormatCloudSubscriptionDisplay(string subscriptionName) =>
        $"{CloudPrefix}{subscriptionName}";

    public static string GetLocalSourceKey(string subscriptionName) =>
        $"{LocalSourcePrefix}{subscriptionName}";

    public static bool IsLocalSourceKey(string key) =>
        key.StartsWith(LocalSourcePrefix, StringComparison.Ordinal);

    public static string GetSourceKeySubscriptionName(string sourceKey) =>
        IsLocalSourceKey(sourceKey) ? sourceKey[LocalSourcePrefix.Length..] : sourceKey;

    public static SubscriptionGroupIdentity ParseGroupDisplay(string? groupDisplay)
    {
        if (string.IsNullOrWhiteSpace(groupDisplay))
        {
            return default;
        }

        var trimmed = groupDisplay.Trim();
        if (string.Equals(trimmed, LocalLabel, StringComparison.OrdinalIgnoreCase))
        {
            return SubscriptionGroupIdentity.LocalManual;
        }

        if (trimmed.StartsWith(LocalPrefix, StringComparison.Ordinal))
        {
            return new SubscriptionGroupIdentity(trimmed[LocalPrefix.Length..].Trim(), IsLocal: true, IsManual: false);
        }

        if (trimmed.StartsWith(CloudPrefix, StringComparison.Ordinal))
        {
            return new SubscriptionGroupIdentity(trimmed[CloudPrefix.Length..].Trim(), IsLocal: false, IsManual: false);
        }

        return new SubscriptionGroupIdentity(trimmed, IsLocal: false, IsManual: false);
    }

    public static string BuildFilterKey(VmessProfile profile)
    {
        if (profile.IsLocalManual)
        {
            return LocalLabel;
        }

        if (string.IsNullOrWhiteSpace(profile.SubscriptionName))
        {
            return LocalLabel;
        }

        if (profile.IsLocalSubscription)
        {
            return GetLocalSourceKey(profile.SubscriptionName);
        }

        return profile.IsCloudManaged
            ? profile.SubscriptionName
            : GetLocalSourceKey(profile.SubscriptionName);
    }

    public static string BuildFilterDisplay(VmessProfile profile) => profile.SubscriptionDisplay;

    public static bool ProfileMatchesScope(VmessProfile profile, SubscriptionGroupIdentity scope)
    {
        if (scope.IsManual)
        {
            return profile.IsLocalManual;
        }

        if (string.IsNullOrWhiteSpace(scope.Name))
        {
            return false;
        }

        if (!string.Equals(profile.SubscriptionName, scope.Name, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return scope.IsLocal ? profile.IsLocalSubscription : profile.IsCloudManaged;
    }

    public static bool IsLocalManualGroupKey(string? groupDisplay) =>
        ParseGroupDisplay(groupDisplay).IsManual;

    public static bool IsLocalSubscriptionGroupDisplay(string? groupDisplay)
    {
        var identity = ParseGroupDisplay(groupDisplay);
        return identity.IsLocal && !identity.IsManual;
    }
}
