namespace NaiwaProxy.Models;

public sealed class SubscriptionSource
{
    public string Url { get; set; } = "";
    public int? AutoRefreshMinutes { get; set; }
    public int? ServerSubscriptionId { get; set; }
    public string? DisplayName { get; set; }
    public DateTime? CreatedAtUtc { get; set; }
    public bool TrafficExhausted { get; set; }
}
