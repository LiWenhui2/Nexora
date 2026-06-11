using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace NaiwaProxy.Models;

public sealed class VmessProfile : INotifyPropertyChanged
{
    private bool _isTcpLatencyTesting;
    private bool _tcpLatencyTested;
    private int? _tcpLatencyMs;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New VMess Server";
    public string Address { get; set; } = "";
    public int Port { get; set; } = 443;
    public string UserId { get; set; } = "";
    public int AlterId { get; set; }
    public string Security { get; set; } = "auto";
    public string Network { get; set; } = "tcp";
    public string Type { get; set; } = "none";
    public string Host { get; set; } = "";
    public string Path { get; set; } = "";
    public string Tls { get; set; } = "";
    public string Sni { get; set; } = "";
    public string Remark { get; set; } = "";
    public string SubscriptionName { get; set; } = "";
    public DateTime? SubscriptionUpdatedAt { get; set; }

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
    public string ListDisplayName => IsActive ? $"★ {DisplayName}" : DisplayName;

    [JsonIgnore]
    public string Endpoint => $"{Address}:{Port}";

    [JsonIgnore]
    public string ProtocolDisplay => "VMess";

    [JsonIgnore]
    public string RegionDisplay => "-";

    [JsonIgnore]
    public string SubscriptionDisplay => string.IsNullOrWhiteSpace(SubscriptionName) ? "手动" : SubscriptionName;

    [JsonIgnore]
    public string UpdatedDisplay
    {
        get
        {
            if (SubscriptionUpdatedAt is null)
            {
                return "-";
            }

            var elapsed = DateTime.Now - SubscriptionUpdatedAt.Value;
            if (elapsed.TotalMinutes < 1)
            {
                return "刚刚";
            }

            if (elapsed.TotalHours < 1)
            {
                return $"{(int)elapsed.TotalMinutes} 分钟前";
            }

            if (elapsed.TotalDays < 1)
            {
                return $"{(int)elapsed.TotalHours} 小时前";
            }

            return $"{(int)elapsed.TotalDays} 天前";
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
        OnPropertyChanged(nameof(ListDisplayName));
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
    }

    public void ResetLatency()
    {
        _tcpLatencyTested = false;
        _tcpLatencyMs = null;
        _isTcpLatencyTesting = false;
        OnPropertyChanged(nameof(IsTcpLatencyTesting));
        OnPropertyChanged(nameof(TcpLatencyMs));
        OnPropertyChanged(nameof(TcpLatencyDisplay));
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

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
