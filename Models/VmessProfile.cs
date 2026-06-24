using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using NaiwaProxy.Services;

namespace NaiwaProxy.Models;

public sealed class VmessProfile : INotifyPropertyChanged
{
    private bool _isTcpLatencyTesting;
    private bool _tcpLatencyTested;
    private int? _tcpLatencyMs;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Protocol { get; set; } = "vmess";
    public string Name { get; set; } = "New VMess Server";
    public string Address { get; set; } = "";
    public int Port { get; set; } = 443;
    public string UserId { get; set; } = "";
    public string Password { get; set; } = "";
    public int AlterId { get; set; }
    public string Security { get; set; } = "auto";
    public string Network { get; set; } = "tcp";
    public string Type { get; set; } = "none";
    public string Host { get; set; } = "";
    public string Path { get; set; } = "";
    public string Tls { get; set; } = "";
    public string Sni { get; set; } = "";
    public string Remark { get; set; } = "";
    public string Region { get; set; } = "";
    public string SubscriptionName { get; set; } = "";
    public DateTime? SubscriptionUpdatedAt { get; set; }
    public long SubscriptionUploadBytes { get; set; }
    public long SubscriptionDownloadBytes { get; set; }
    public long? SubscriptionTotalBytes { get; set; }
    public DateTime? XpanelExpiryTime { get; set; }
    public long? XpanelTotalBytes { get; set; }
    public long? XpanelUsedBytes { get; set; }
    public long? XpanelRemainingBytes { get; set; }
    public DateTime? UpdatedAt { get; set; }

    [JsonIgnore]
    public bool IsTcpLatencyTesting
    {
        get => _isTcpLatencyTesting;
        private set => SetLatencyTestingField(ref _isTcpLatencyTesting, value, nameof(TcpLatencyDisplay));
    }

    [JsonIgnore]
    public int? TcpLatencyMs
    {
        get => _tcpLatencyMs;
        private set => SetLatencyValueField(ref _tcpLatencyMs, value, nameof(TcpLatencyDisplay));
    }

    [JsonIgnore]
    public string TcpLatencyDisplay => FormatLatencyDisplay(IsTcpLatencyTesting, _tcpLatencyTested, TcpLatencyMs);

    private bool _isActive;

    [JsonIgnore]
    public bool IsActive => _isActive;

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? $"{Address}:{Port}" : Name;

    [JsonIgnore]
    public string ListDisplayName => DisplayName;

    [JsonIgnore]
    public string Endpoint => $"{Address}:{Port}";

    [JsonIgnore]
    public string ProtocolDisplay => Protocol.ToLowerInvariant() switch
    {
        "vless" => "VLESS",
        "trojan" => "Trojan",
        "shadowsocks" or "ss" => "Shadowsocks",
        "socks" or "socks5" => "SOCKS",
        "http" or "https" => "HTTP",
        _ => "VMess"
    };

    [JsonIgnore]
    public string RegionDisplay => NodeRegionHelper.FormatDisplay(
        string.IsNullOrWhiteSpace(Region) ? NodeRegionHelper.Resolve(this) : Region);

    [JsonIgnore]
    public string RegionCountryDisplay => ToCountryOnly(RegionDisplay);

    [JsonIgnore]
    public string StatusDisplay
    {
        get
        {
            if (IsExpired)
            {
                return "过期";
            }

            if (_tcpLatencyTested && TcpLatencyMs is null)
            {
                return "超时";
            }

            return IsActive ? "当前" : "可用";
        }
    }

    [JsonIgnore]
    public string SubscriptionDisplay => string.IsNullOrWhiteSpace(SubscriptionName) ? "手动" : SubscriptionName;

    [JsonIgnore]
    public string SubscriptionRemainingDisplay
    {
        get
        {
            if (string.IsNullOrWhiteSpace(SubscriptionName) || SubscriptionTotalBytes is not long total)
            {
                return "-";
            }

            var remaining = Math.Max(0, total - SubscriptionUploadBytes - SubscriptionDownloadBytes);
            return FormatBytes(remaining);
        }
    }

    [JsonIgnore]
    public string ExpiryDisplay => FormatExpiryDisplay(XpanelExpiryTime);

    [JsonIgnore]
    public string TotalTrafficDisplay => FormatTotalTrafficDisplay(XpanelTotalBytes);

    [JsonIgnore]
    public bool ShouldShowRemainingTraffic =>
        !string.IsNullOrWhiteSpace(SubscriptionName) &&
        (HasXpanelTrafficMetadata() || SubscriptionTotalBytes is not null);

    [JsonIgnore]
    public string RemainingTrafficDisplay
    {
        get
        {
            if (!ShouldShowRemainingTraffic)
            {
                return "";
            }

            if (HasXpanelTrafficMetadata())
            {
                return FormatRemainingTrafficDisplay(
                    XpanelRemainingBytes,
                    XpanelTotalBytes,
                    XpanelUsedBytes);
            }

            return SubscriptionRemainingDisplay;
        }
    }

    [JsonIgnore]
    public bool IsExpired
    {
        get
        {
            if (XpanelExpiryTime is not DateTime expiryUtc)
            {
                return false;
            }

            var local = expiryUtc.ToLocalTime();
            return local.Year < 2099 && local <= DateTime.Now;
        }
    }

    private bool HasXpanelTrafficMetadata() =>
        XpanelExpiryTime is not null ||
        XpanelTotalBytes is not null ||
        XpanelUsedBytes is not null ||
        XpanelRemainingBytes is not null;

    [JsonIgnore]
    public string UpdatedDisplay
    {
        get
        {
            var updatedAt = SubscriptionUpdatedAt ?? UpdatedAt;
            return updatedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "-";
        }
    }

    [JsonIgnore]
    public string PickerDisplay
    {
        get
        {
            var tls = string.IsNullOrWhiteSpace(Tls) ? "" : " · TLS";
            return $"{DisplayName} · {ProtocolDisplay}{tls}";
        }
    }

    [JsonIgnore]
    public string NodeAddressDisplay => $"[{ProtocolDisplay}] {Endpoint}";

    public void SetActive(bool value)
    {
        if (_isActive == value)
        {
            return;
        }

        _isActive = value;
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(StatusDisplay));
    }

    public void SetRegion(string region)
    {
        if (Region == region)
        {
            return;
        }

        Region = region;
        OnPropertyChanged(nameof(RegionDisplay));
        OnPropertyChanged(nameof(RegionCountryDisplay));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void BeginTcpLatencyTest()
    {
        IsTcpLatencyTesting = true;
    }

    public void CompleteTcpLatencyTest(int? latencyMs)
    {
        IsTcpLatencyTesting = false;
        _tcpLatencyTested = true;
        TcpLatencyMs = latencyMs;
        OnPropertyChanged(nameof(TcpLatencyDisplay));
        OnPropertyChanged(nameof(StatusDisplay));
    }

    public void ResetLatency()
    {
        _tcpLatencyTested = false;
        _tcpLatencyMs = null;
        _isTcpLatencyTesting = false;
        OnPropertyChanged(nameof(IsTcpLatencyTesting));
        OnPropertyChanged(nameof(TcpLatencyMs));
        OnPropertyChanged(nameof(TcpLatencyDisplay));
        OnPropertyChanged(nameof(StatusDisplay));
    }

    private void SetLatencyTestingField(ref bool field, bool value, string displayPropertyName)
    {
        if (field == value)
        {
            return;
        }

        field = value;
        OnPropertyChanged(displayPropertyName);
    }

    private void SetLatencyValueField(ref int? field, int? value, string displayPropertyName)
    {
        if (field == value)
        {
            return;
        }

        field = value;
        OnPropertyChanged(displayPropertyName);
    }

    private static string FormatLatencyDisplay(bool isTesting, bool tested, int? latencyMs)
    {
        if (isTesting)
        {
            return "...";
        }

        if (!tested)
        {
            return "-";
        }

        return latencyMs is null ? "Timeout" : $"{latencyMs} ms";
    }

    private static string FormatExpiryDisplay(DateTime? expiryUtc)
    {
        if (expiryUtc is null)
        {
            return "-";
        }

        var local = expiryUtc.Value.ToLocalTime();
        if (local.Year >= 2099)
        {
            return "永久";
        }

        var formatted = local.ToString("yyyy-MM-dd HH:mm");
        return local <= DateTime.Now ? $"{formatted}（已过期）" : formatted;
    }

    private static string FormatTotalTrafficDisplay(long? totalBytes)
    {
        if (totalBytes is null)
        {
            return "-";
        }

        return totalBytes == 0 ? "无限制" : FormatBytes(totalBytes.Value);
    }

    private static string FormatRemainingTrafficDisplay(long? remainingBytes, long? totalBytes, long? usedBytes)
    {
        if (totalBytes == 0)
        {
            return "无限制";
        }

        if (remainingBytes is long remaining)
        {
            return FormatBytes(remaining);
        }

        if (totalBytes is long total && usedBytes is long used)
        {
            return FormatBytes(Math.Max(0, total - used));
        }

        if (totalBytes is long totalOnly)
        {
            return FormatBytes(totalOnly);
        }

        return "-";
    }

    private static string ToCountryOnly(string region)
    {
        if (string.IsNullOrWhiteSpace(region) || region == "-")
        {
            return "-";
        }

        var trimmed = region.Trim();
        foreach (var separator in new[] { " · ", " / ", " - ", " | ", "，", ",", "・", "·" })
        {
            var index = trimmed.IndexOf(separator, StringComparison.Ordinal);
            if (index > 0)
            {
                return trimmed[..index].Trim();
            }
        }

        return trimmed;
    }

    private static string FormatBytes(double bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.##} {units[unit]}";
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
