namespace NaiwaProxy.Models;

public sealed class AppSettings
{
    public int SocksPort { get; set; } = 10808;
    public int HttpPort { get; set; } = 10809;
    public int ApiPort { get; set; } = 10810;
    public string CoreExecutable { get; set; } = "xray.exe";
    public List<VmessProfile> Profiles { get; set; } = [];
    public string? SelectedProfileId { get; set; }
    public string SystemProxyMode { get; set; } = "Auto";
    public string RoutingMode { get; set; } = "BypassChina";
    public CustomRoutingSettings CustomRouting { get; set; } = new();
    public bool IsTunEnabled { get; set; }
    public long TotalUplinkBytes { get; set; }
    public long TotalDownlinkBytes { get; set; }
    public string TodayTrafficDate { get; set; } = "";
    public long TodayUplinkBytes { get; set; }
    public long TodayDownlinkBytes { get; set; }
    public long SubscriptionUploadBytes { get; set; }
    public long SubscriptionDownloadBytes { get; set; }
    public long? SubscriptionTotalBytes { get; set; }
    public string AuthApiBaseUrl { get; set; } = "http://43.136.117.106:8080/api/v1";
    public bool RunAtStartup { get; set; }
    public bool RunAtStartupSilent { get; set; }
    public bool AllowLanAccess { get; set; }
    public string ThemeAccentColor { get; set; } = "#2563EB";
    public Dictionary<string, SubscriptionSource> SubscriptionSources { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class CustomRoutingSettings
{
    public List<string> ProxyDomains { get; set; } = [];
    public List<string> DirectDomains { get; set; } = [];
    public List<string> BlockDomains { get; set; } = [];
    public List<string> ProxyIps { get; set; } = [];
    public List<string> DirectIps { get; set; } = [];
    public List<string> BlockIps { get; set; } = [];
}
