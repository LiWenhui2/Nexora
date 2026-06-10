using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace NaiwaProxy.Models;

public sealed class VmessProfile : INotifyPropertyChanged
{
    private bool _isTcpLatencyTesting;
    private bool _isRealLatencyTesting;
    private bool _tcpLatencyTested;
    private bool _realLatencyTested;
    private int? _tcpLatencyMs;
    private int? _realLatencyMs;

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

    [JsonIgnore]
    public bool IsTcpLatencyTesting
    {
        get => _isTcpLatencyTesting;
        private set => SetLatencyTestingField(ref _isTcpLatencyTesting, value, nameof(TcpLatencyDisplay));
    }

    [JsonIgnore]
    public bool IsRealLatencyTesting
    {
        get => _isRealLatencyTesting;
        private set => SetLatencyTestingField(ref _isRealLatencyTesting, value, nameof(RealLatencyDisplay));
    }

    [JsonIgnore]
    public int? TcpLatencyMs
    {
        get => _tcpLatencyMs;
        private set => SetLatencyValueField(ref _tcpLatencyMs, value, nameof(TcpLatencyDisplay));
    }

    [JsonIgnore]
    public int? RealLatencyMs
    {
        get => _realLatencyMs;
        private set => SetLatencyValueField(ref _realLatencyMs, value, nameof(RealLatencyDisplay));
    }

    [JsonIgnore]
    public string TcpLatencyDisplay => FormatLatencyDisplay(IsTcpLatencyTesting, _tcpLatencyTested, TcpLatencyMs);

    [JsonIgnore]
    public string RealLatencyDisplay => FormatLatencyDisplay(IsRealLatencyTesting, _realLatencyTested, RealLatencyMs);

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? $"{Address}:{Port}" : Name;

    [JsonIgnore]
    public string Endpoint => $"{Address}:{Port}";

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

    public void BeginRealLatencyTest()
    {
        IsRealLatencyTesting = true;
    }

    public void CompleteRealLatencyTest(int? latencyMs)
    {
        IsRealLatencyTesting = false;
        _realLatencyTested = true;
        RealLatencyMs = latencyMs;
        OnPropertyChanged(nameof(RealLatencyDisplay));
    }

    public void ResetLatency()
    {
        _tcpLatencyTested = false;
        _realLatencyTested = false;
        _tcpLatencyMs = null;
        _realLatencyMs = null;
        _isTcpLatencyTesting = false;
        _isRealLatencyTesting = false;
        OnPropertyChanged(nameof(IsTcpLatencyTesting));
        OnPropertyChanged(nameof(IsRealLatencyTesting));
        OnPropertyChanged(nameof(TcpLatencyMs));
        OnPropertyChanged(nameof(RealLatencyMs));
        OnPropertyChanged(nameof(TcpLatencyDisplay));
        OnPropertyChanged(nameof(RealLatencyDisplay));
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
