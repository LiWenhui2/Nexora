namespace NaiwaProxy.Models;

public sealed class AppUpdateFile
{
    public string Platform { get; set; } = "";
    public string Filename { get; set; } = "";
    public long FileSize { get; set; }
    public string Sha256 { get; set; } = "";
    public string DownloadUrl { get; set; } = "";
    public string SignatureThumbprint { get; set; } = "";
}

public sealed class AppUpdateRelease
{
    public int Id { get; set; }
    public int VersionCode { get; set; }
    public string VersionName { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Content { get; set; }
    public bool ForceUpdate { get; set; }
    public string PublishedAt { get; set; } = "";
    public AppUpdateFile? File { get; set; }
}

public sealed class VersionUpdatePushMessage
{
    public string Type { get; set; } = "";
    public int ReleaseId { get; set; }
    public int VersionCode { get; set; }
    public string VersionName { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Content { get; set; }
    public bool ForceUpdate { get; set; }
    public bool HasWindows { get; set; }
    public bool HasAndroid { get; set; }
    public long Timestamp { get; set; }
}
