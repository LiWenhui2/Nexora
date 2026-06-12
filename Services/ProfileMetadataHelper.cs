using NaiwaProxy.Models;

namespace NaiwaProxy.Services;

public static class ProfileMetadataHelper
{
    public static void ApplyNew(VmessProfile profile)
    {
        profile.UpdatedAt ??= DateTime.Now;
        var resolved = NodeRegionHelper.Resolve(profile);
        if (resolved != "-")
        {
            profile.SetRegion(resolved);
        }
    }

    public static bool Ensure(VmessProfile profile)
    {
        var changed = false;

        if (string.IsNullOrWhiteSpace(profile.Region))
        {
            var resolved = NodeRegionHelper.Resolve(profile);
            if (resolved != "-")
            {
                profile.SetRegion(resolved);
                changed = true;
            }
        }

        if (profile.UpdatedAt is null && profile.SubscriptionUpdatedAt is null)
        {
            profile.UpdatedAt = DateTime.Now;
            changed = true;
        }

        return changed;
    }
}
