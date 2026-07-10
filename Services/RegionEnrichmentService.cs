using NaiwaProxy.Models;

namespace NaiwaProxy.Services;

public static class RegionEnrichmentService
{
    public static async Task<List<(VmessProfile Profile, string Region)>> CollectRegionUpdatesAsync(
        IEnumerable<VmessProfile> profiles,
        CancellationToken cancellationToken = default)
    {
        var updates = new List<(VmessProfile Profile, string Region)>();

        foreach (var profile in profiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!string.IsNullOrWhiteSpace(profile.Region) && profile.Region != "-")
            {
                continue;
            }

            var keywordRegion = NodeRegionHelper.Resolve(profile);
            if (keywordRegion != "-")
            {
                updates.Add((profile, keywordRegion));
                continue;
            }

            if (string.IsNullOrWhiteSpace(profile.Address))
            {
                continue;
            }

            var ipRegion = await IpRegionService.LookupAsync(profile.Address, cancellationToken);
            if (!string.IsNullOrWhiteSpace(ipRegion))
            {
                updates.Add((profile, ipRegion));
            }
        }

        return updates;
    }

    public static async Task<int> EnrichRegionsAsync(IEnumerable<VmessProfile> profiles, CancellationToken cancellationToken = default)
    {
        var updates = await CollectRegionUpdatesAsync(profiles, cancellationToken);
        foreach (var (profile, region) in updates)
        {
            profile.SetRegion(region);
        }

        return updates.Count;
    }
}
