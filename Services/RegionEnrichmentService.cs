using NaiwaProxy.Models;

namespace NaiwaProxy.Services;

public static class RegionEnrichmentService
{
    public static async Task<int> EnrichRegionsAsync(IEnumerable<VmessProfile> profiles, CancellationToken cancellationToken = default)
    {
        var updated = 0;

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
                profile.SetRegion(keywordRegion);
                updated++;
                continue;
            }

            if (string.IsNullOrWhiteSpace(profile.Address))
            {
                continue;
            }

            var ipRegion = await IpRegionService.LookupAsync(profile.Address, cancellationToken);
            if (!string.IsNullOrWhiteSpace(ipRegion))
            {
                profile.SetRegion(ipRegion);
                updated++;
            }
        }

        return updated;
    }
}
