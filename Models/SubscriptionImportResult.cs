namespace NaiwaProxy.Models;

public sealed class SubscriptionImportResult
{
    public List<VmessProfile> Profiles { get; init; } = [];
    public SubscriptionTrafficInfo? TrafficInfo { get; init; }
    public string? SourceUrl { get; init; }
    public string? SubscriptionName { get; init; }
}

public sealed class SubscriptionTrafficInfo
{
    public long UploadBytes { get; init; }
    public long DownloadBytes { get; init; }
    public long? TotalBytes { get; init; }
    public DateTime? ExpireAtUtc { get; init; }

    public long? RemainingBytes => TotalBytes is long total
        ? Math.Max(0, total - UploadBytes - DownloadBytes)
        : null;
}
