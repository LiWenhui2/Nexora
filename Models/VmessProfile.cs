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
    private int? _displayLatencyMs;

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
    public string Flow { get; set; } = "";
    public string RealityPublicKey { get; set; } = "";
    public string RealityShortId { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public string RealitySpiderX { get; set; } = "";
    public string Sni { get; set; } = "";
    public string Remark { get; set; } = "";
    public string Region { get; set; } = "";
    public string SubscriptionName { get; set; } = "";
    public bool IsCloudManaged { get; set; }
    public bool IsLocalManual { get; set; }
    public bool IsLocalSubscription { get; set; }
    public DateTime? SubscriptionUpdatedAt { get; set; }
    public long SubscriptionUploadBytes { get; set; }
    public long SubscriptionDownloadBytes { get; set; }
    public long? SubscriptionTotalBytes { get; set; }
    public DateTime? XpanelExpiryTime { get; set; }
    public long? XpanelTotalBytes { get; set; }
    public long? XpanelUsedBytes { get; set; }
    public long? XpanelRemainingBytes { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public bool? LastTcpConnectSuccess { get; set; }
    public bool? LastProxyHandshakeSuccess { get; set; }
    public bool? LastWebsiteAccessSuccess { get; set; }
    public double? LastDownloadSpeedMbps { get; set; }
    public int? LastStabilityPercent { get; set; }
    public DateTime? LastHealthSuccessAt { get; set; }

    [JsonIgnore]
    public bool IsTcpLatencyTesting
    {
        get => _isTcpLatencyTesting;
        private set => SetLatencyTestingField(ref _isTcpLatencyTesting, value);
    }

    [JsonIgnore]
    public int? TcpLatencyMs
    {
        get => _tcpLatencyMs;
        private set => SetLatencyValueField(ref _tcpLatencyMs, value);
    }

    [JsonIgnore]
    public int? DisplayLatencyMs => _displayLatencyMs;

    [JsonIgnore]
    public string TcpLatencyDisplay
    {
        get
        {
            if (_displayLatencyMs is int ms)
            {
                return $"{ms} ms";
            }

            if (_tcpLatencyTested && _tcpLatencyMs is null)
            {
                return "Timeout";
            }

            return "";
        }
    }

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
    public string SubscriptionDisplay
    {
        get
        {
            if (IsLocalManual || (string.IsNullOrWhiteSpace(SubscriptionName) && !IsCloudManaged))
            {
                return LocalSubscriptionHelper.LocalLabel;
            }

            if (IsLocalSubscription && !string.IsNullOrWhiteSpace(SubscriptionName))
            {
                return LocalSubscriptionHelper.FormatLocalSubscriptionDisplay(SubscriptionName);
            }

            return string.IsNullOrWhiteSpace(SubscriptionName)
                ? LocalSubscriptionHelper.LocalLabel
                : SubscriptionName;
        }
    }

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
    public string HealthSummaryDisplay
    {
        get
        {
            if (LastTcpConnectSuccess is null) return "未检测";
            var speed = LastDownloadSpeedMbps is double mbps ? $" · {mbps:0.0} Mbps" : "";
            return $"TCP{Mark(LastTcpConnectSuccess)} 握手{Mark(LastProxyHandshakeSuccess)} 网站{Mark(LastWebsiteAccessSuccess)} · 稳定 {LastStabilityPercent ?? 0}%{speed}";
        }
    }

    [JsonIgnore]
    public string LastHealthSuccessDisplay => LastHealthSuccessAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "-";

    [JsonIgnore]
    public string PickerDisplay
    {
        get
        {
            var tls = Tls.ToLowerInvariant() switch
            {
                "tls" => " · TLS",
                "reality" => " · Reality",
                _ => ""
            };
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

    public void SetLatencyResult(int? latencyMs)
    {
        _isTcpLatencyTesting = false;
        _tcpLatencyTested = true;
        _tcpLatencyMs = latencyMs;
        _displayLatencyMs = latencyMs;
    }

    public bool TryApplyLatencyResult(int? latencyMs)
    {
        var previousDisplay = TcpLatencyDisplay;
        var previousStatus = StatusDisplay;
        var latencyChanged = _tcpLatencyMs != latencyMs;

        SetLatencyResult(latencyMs);

        if (!latencyChanged &&
            string.Equals(TcpLatencyDisplay, previousDisplay, StringComparison.Ordinal) &&
            string.Equals(StatusDisplay, previousStatus, StringComparison.Ordinal))
        {
            return false;
        }

        if (latencyChanged)
        {
            OnPropertyChanged(nameof(TcpLatencyMs));
            OnPropertyChanged(nameof(DisplayLatencyMs));
        }

        if (!string.Equals(TcpLatencyDisplay, previousDisplay, StringComparison.Ordinal))
        {
            OnPropertyChanged(nameof(TcpLatencyDisplay));
        }

        if (!string.Equals(StatusDisplay, previousStatus, StringComparison.Ordinal))
        {
            OnPropertyChanged(nameof(StatusDisplay));
        }

        return true;
    }

    public void NotifyLatencyDisplayChanged()
    {
        OnPropertyChanged(nameof(IsTcpLatencyTesting));
        OnPropertyChanged(nameof(TcpLatencyMs));
        OnPropertyChanged(nameof(DisplayLatencyMs));
        OnPropertyChanged(nameof(TcpLatencyDisplay));
        OnPropertyChanged(nameof(StatusDisplay));
    }

    public void CompleteTcpLatencyTest(int? latencyMs)
    {
        var previousDisplay = TcpLatencyDisplay;
        var previousStatus = StatusDisplay;
        var wasTesting = _isTcpLatencyTesting;
        var latencyChanged = _tcpLatencyMs != latencyMs;

        SetLatencyResult(latencyMs);

        if (wasTesting)
        {
            OnPropertyChanged(nameof(IsTcpLatencyTesting));
        }

        if (latencyChanged)
        {
            OnPropertyChanged(nameof(TcpLatencyMs));
            OnPropertyChanged(nameof(DisplayLatencyMs));
        }

        if (!string.Equals(TcpLatencyDisplay, previousDisplay, StringComparison.Ordinal))
        {
            OnPropertyChanged(nameof(TcpLatencyDisplay));
        }

        if (!string.Equals(StatusDisplay, previousStatus, StringComparison.Ordinal))
        {
            OnPropertyChanged(nameof(StatusDisplay));
        }
    }

    public void ResetLatency()
    {
        _tcpLatencyTested = false;
        _tcpLatencyMs = null;
        _displayLatencyMs = null;
        _isTcpLatencyTesting = false;
        OnPropertyChanged(nameof(IsTcpLatencyTesting));
        OnPropertyChanged(nameof(TcpLatencyMs));
        OnPropertyChanged(nameof(DisplayLatencyMs));
        OnPropertyChanged(nameof(TcpLatencyDisplay));
        OnPropertyChanged(nameof(StatusDisplay));
    }

    public void NotifyListDisplayChanged()
    {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(ListDisplayName));
        OnPropertyChanged(nameof(ProtocolDisplay));
        OnPropertyChanged(nameof(Address));
        OnPropertyChanged(nameof(Port));
        OnPropertyChanged(nameof(ExpiryDisplay));
        OnPropertyChanged(nameof(TotalTrafficDisplay));
        OnPropertyChanged(nameof(RemainingTrafficDisplay));
        OnPropertyChanged(nameof(UpdatedDisplay));
        OnPropertyChanged(nameof(StatusDisplay));
        OnPropertyChanged(nameof(HealthSummaryDisplay));
        OnPropertyChanged(nameof(LastHealthSuccessDisplay));
    }

    public void ApplyHealthResult(NodeHealthResult result)
    {
        LastTcpConnectSuccess = result.TcpConnectSuccess;
        LastProxyHandshakeSuccess = result.ProxyHandshakeSuccess;
        LastWebsiteAccessSuccess = result.WebsiteAccessSuccess;
        LastDownloadSpeedMbps = result.DownloadSpeedMbps;
        LastStabilityPercent = result.StabilityPercent;
        if (result.Success) LastHealthSuccessAt = DateTime.Now;
        OnPropertyChanged(nameof(HealthSummaryDisplay));
        OnPropertyChanged(nameof(LastHealthSuccessDisplay));
    }

    private static string Mark(bool? value) => value == true ? "✓" : "✕";

    private void SetLatencyTestingField(ref bool field, bool value)
    {
        if (field == value)
        {
            return;
        }

        field = value;
        OnPropertyChanged(nameof(IsTcpLatencyTesting));
    }

    private void SetLatencyValueField(ref int? field, int? value)
    {
        if (field == value)
        {
            return;
        }

        field = value;
        OnPropertyChanged(nameof(TcpLatencyMs));
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
