using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace NaiwaProxy.Models;

public enum WebsiteTestState
{
    Pending,
    Testing,
    Success,
    Failed
}

public sealed class WebsiteTestItem : INotifyPropertyChanged
{
    public WebsiteTestItem(string name, string url, string iconFileName)
    {
        Name = name;
        Url = url;
        LogoPath = Path.Combine(AppContext.BaseDirectory, "assets", "site-icons", iconFileName);
    }

    public string Name { get; }
    public string Url { get; }
    public string LogoPath { get; }

    private WebsiteTestState _state = WebsiteTestState.Pending;
    private int? _latencyMs;
    private string? _errorMessage;

    public WebsiteTestState State
    {
        get => _state;
        private set
        {
            if (_state == value)
            {
                return;
            }

            _state = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusDisplay));
        }
    }

    public int? LatencyMs
    {
        get => _latencyMs;
        private set
        {
            if (_latencyMs == value)
            {
                return;
            }

            _latencyMs = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusDisplay));
        }
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (_errorMessage == value)
            {
                return;
            }

            _errorMessage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusDisplay));
        }
    }

    public string StatusDisplay => State switch
    {
        WebsiteTestState.Pending => "待测试",
        WebsiteTestState.Testing => "测试中…",
        WebsiteTestState.Success when LatencyMs is not null => $"{LatencyMs} ms",
        WebsiteTestState.Failed => string.IsNullOrWhiteSpace(ErrorMessage) ? "失败" : ErrorMessage,
        _ => "—"
    };

    public void BeginTest()
    {
        State = WebsiteTestState.Testing;
        LatencyMs = null;
        ErrorMessage = null;
    }

    public void CompleteSuccess(int latencyMs)
    {
        LatencyMs = latencyMs;
        ErrorMessage = null;
        State = WebsiteTestState.Success;
    }

    public void CompleteFailure(string? message = null)
    {
        LatencyMs = null;
        ErrorMessage = string.IsNullOrWhiteSpace(message) ? "失败" : message;
        State = WebsiteTestState.Failed;
    }

    public void Reset()
    {
        State = WebsiteTestState.Pending;
        LatencyMs = null;
        ErrorMessage = null;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
