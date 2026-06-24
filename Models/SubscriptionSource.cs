namespace NaiwaProxy.Models;

public sealed class SubscriptionSource
{
    public string Url { get; set; } = "";
    public int? AutoRefreshMinutes { get; set; }
}
