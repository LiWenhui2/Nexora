using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using NaiwaProxy.Models;

namespace NaiwaProxy.Services;

public sealed class BackendWebSocketService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly Func<Task<string?>> _getAccessTokenAsync;
    private readonly Func<string> _getBaseUrl;
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _lifecycleCts;
    private Task? _receiveTask;
    private int _receiveLoopThreadId;
    private bool _disposed;

    public BackendWebSocketService(Func<Task<string?>> getAccessTokenAsync, Func<string> getBaseUrl)
    {
        _getAccessTokenAsync = getAccessTokenAsync;
        _getBaseUrl = getBaseUrl;
    }

    public bool IsConnected => _socket?.State == WebSocketState.Open;

    public event Action<ChatMessage>? ChatMessageReceived;
    public event Action<BroadcastPushMessage>? BroadcastReceived;
    public event Action<VersionUpdatePushMessage>? VersionUpdateReceived;
    public event Action? UserProfileUpdated;
    public event Action<ForceLogoutPushMessage>? ForceLogoutReceived;
    public event Action<TokenExpiredPushMessage>? TokenExpiredReceived;
    public event Action<HeartbeatTimeoutPushMessage>? HeartbeatTimeoutReceived;
    public event Action? Connected;
    public event Action? Disconnected;
    public event Action<string>? ConnectionStateChanged;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _connectLock.WaitAsync(cancellationToken);
        try
        {
            if (IsConnected)
            {
                return;
            }

            await DisconnectCoreAsync();

            var token = await _getAccessTokenAsync();
            if (string.IsNullOrWhiteSpace(token))
            {
                return;
            }

            ConnectionStateChanged?.Invoke("connecting");
            var wsUrl = BuildWebSocketUrl(token);
            _lifecycleCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _socket = new ClientWebSocket();
            await _socket.ConnectAsync(new Uri(wsUrl), _lifecycleCts.Token);
            _receiveTask = Task.Run(() => ReceiveLoopAsync(_lifecycleCts.Token), CancellationToken.None);
            ConnectionStateChanged?.Invoke("connected");
            Connected?.Invoke();
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Warning($"Backend WebSocket connect failed: {ex.Message}");
            ConnectionStateChanged?.Invoke("disconnected");
            await DisconnectCoreAsync();
        }
        finally
        {
            _connectLock.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        await _connectLock.WaitAsync();
        try
        {
            await DisconnectCoreAsync();
        }
        finally
        {
            _connectLock.Release();
        }
    }

    public async Task SendPingAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConnected || _socket is null)
        {
            return;
        }

        try
        {
            var payload = Encoding.UTF8.GetBytes("""{"type":"PING"}""");
            await _socket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken);
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Warning($"Backend WebSocket ping failed: {ex.Message}");
            await DisconnectAsync();
        }
    }

    private string BuildWebSocketUrl(string accessToken)
    {
        var baseUrl = _getBaseUrl().TrimEnd('/');
        var httpUri = new Uri(baseUrl);
        var scheme = string.Equals(httpUri.Scheme, "https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws";
        var builder = new UriBuilder(httpUri)
        {
            Scheme = scheme,
            Path = $"{httpUri.AbsolutePath.TrimEnd('/')}/ws",
            Query = $"token={Uri.EscapeDataString(accessToken)}"
        };
        return builder.Uri.ToString();
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        _receiveLoopThreadId = Environment.CurrentManagedThreadId;
        var buffer = new byte[8192];
        var builder = new StringBuilder();

        try
        {
            while (!cancellationToken.IsCancellationRequested && _socket?.State == WebSocketState.Open)
            {
                builder.Clear();
                WebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(buffer, cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        ScheduleDisconnect();
                        return;
                    }

                    builder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                }
                while (!result.EndOfMessage);

                HandleMessage(builder.ToString());
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Warning($"Backend WebSocket receive failed: {ex.Message}");
        }
        finally
        {
            if (_receiveLoopThreadId == Environment.CurrentManagedThreadId)
            {
                _receiveLoopThreadId = 0;
            }

            ConnectionStateChanged?.Invoke("disconnected");
            Disconnected?.Invoke();
        }
    }

    private void HandleMessage(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("type", out var typeElement))
            {
                return;
            }

            var type = typeElement.GetString();
            switch (type?.ToUpperInvariant())
            {
                case "AUTH_SUCCESS":
                    ConnectionStateChanged?.Invoke("connected");
                    break;
                case "CHAT_MESSAGE":
                    var wsChatMessage = JsonSerializer.Deserialize<WebSocketChatMessage>(json, JsonOptions);
                    if (wsChatMessage is not null)
                    {
                        ChatMessageReceived?.Invoke(wsChatMessage.ToChatMessage());
                    }
                    break;
                case "BROADCAST":
                    var broadcast = JsonSerializer.Deserialize<BroadcastPushMessage>(json, JsonOptions);
                    if (broadcast is not null)
                    {
                        BroadcastReceived?.Invoke(broadcast);
                    }
                    break;
                case "VERSION_UPDATE":
                    var versionUpdate = JsonSerializer.Deserialize<VersionUpdatePushMessage>(json, JsonOptions);
                    if (versionUpdate is not null)
                    {
                        VersionUpdateReceived?.Invoke(versionUpdate);
                    }
                    break;
                case "USER_PROFILE_UPDATED":
                    UserProfileUpdated?.Invoke();
                    break;
                case "FORCE_LOGOUT":
                    var forceLogout = JsonSerializer.Deserialize<ForceLogoutPushMessage>(json, JsonOptions);
                    if (forceLogout is not null)
                    {
                        DiagnosticLogService.Info(
                            $"FORCE_LOGOUT received. reason={forceLogout.Reason ?? "-"}, kickedSessionId={forceLogout.ResolvedKickedSessionId ?? "-"}");
                        ForceLogoutReceived?.Invoke(forceLogout);
                    }
                    ScheduleDisconnect();
                    break;
                case "TOKEN_EXPIRED":
                    var tokenExpired = JsonSerializer.Deserialize<TokenExpiredPushMessage>(json, JsonOptions);
                    if (tokenExpired is not null)
                    {
                        DiagnosticLogService.Info($"TOKEN_EXPIRED received. message={tokenExpired.Message ?? "-"}");
                        TokenExpiredReceived?.Invoke(tokenExpired);
                    }
                    ScheduleDisconnect();
                    break;
                case "HEARTBEAT_TIMEOUT":
                    var heartbeatTimeout = JsonSerializer.Deserialize<HeartbeatTimeoutPushMessage>(json, JsonOptions);
                    if (heartbeatTimeout is not null)
                    {
                        DiagnosticLogService.Info($"HEARTBEAT_TIMEOUT received. message={heartbeatTimeout.Message ?? "-"}");
                        HeartbeatTimeoutReceived?.Invoke(heartbeatTimeout);
                    }
                    ScheduleDisconnect();
                    break;
            }
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Warning($"Backend WebSocket message parse failed: {ex.Message}");
        }
    }

    private void ScheduleDisconnect()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await DisconnectAsync();
            }
            catch (Exception ex)
            {
                DiagnosticLogService.Warning($"Backend WebSocket disconnect failed: {ex.Message}");
            }
        });
    }

    private async Task DisconnectCoreAsync()
    {
        if (_lifecycleCts is not null)
        {
            await _lifecycleCts.CancelAsync();
            _lifecycleCts.Dispose();
            _lifecycleCts = null;
        }

        var receiveTask = _receiveTask;
        _receiveTask = null;
        if (receiveTask is not null &&
            Environment.CurrentManagedThreadId != _receiveLoopThreadId)
        {
            try
            {
                await receiveTask;
            }
            catch
            {
            }
        }

        if (_socket is not null)
        {
            if (_socket.State == WebSocketState.Open || _socket.State == WebSocketState.CloseReceived)
            {
                try
                {
                    await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "disconnect", CancellationToken.None);
                }
                catch
                {
                }
            }

            _socket.Dispose();
            _socket = null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _ = DisconnectAsync();
        _connectLock.Dispose();
    }
}

public sealed class ForceLogoutDeviceInfo
{
    public string? SessionId { get; set; }
    public string? ClientType { get; set; }
    public string? DeviceId { get; set; }
    public string? DeviceName { get; set; }
    public string? OsName { get; set; }
    public string? OsVersion { get; set; }
    public string? AppVersion { get; set; }
    public string? IpAddress { get; set; }
    public string? Country { get; set; }
    public string? Region { get; set; }
    public string? City { get; set; }
    public string? Isp { get; set; }
}

public sealed class ForceLogoutPushMessage
{
    public string Type { get; set; } = "";
    public string? Reason { get; set; }
    public int? MaxActiveDevices { get; set; }
    public string? Message { get; set; }
    public string? KickedSessionId { get; set; }
    public string? SessionId { get; set; }
    public ForceLogoutDeviceInfo? NewLoginDevice { get; set; }
    public string? LockedUntil { get; set; }
    public long Timestamp { get; set; }

    public string? ResolvedKickedSessionId =>
        !string.IsNullOrWhiteSpace(KickedSessionId) ? KickedSessionId : SessionId;

    public string GetDisplayMessage()
    {
        if (!string.IsNullOrWhiteSpace(Message))
        {
            return Message.Trim();
        }

        if (IsAdminKickout)
        {
            return "当前设备已被管理员强制下线，请重新登录。";
        }

        if (IsAccountDisabled)
        {
            return "当前账号已被管理员禁用";
        }

        if (IsAccountLocked)
        {
            return "当前账号已被管理员锁定";
        }

        if (string.Equals(Reason, "DEVICE_LIMIT_EXCEEDED", StringComparison.OrdinalIgnoreCase))
        {
            return "当前账号已在新设备登录，登录设备数量超过限制，此设备已被下线。";
        }

        return "当前账号登录状态已失效，请重新登录。";
    }

    public bool IsAdminKickout =>
        string.Equals(Reason, "ADMIN_KICKOUT", StringComparison.OrdinalIgnoreCase);

    public bool IsAccountDisabled =>
        string.Equals(Reason, "ACCOUNT_DISABLED", StringComparison.OrdinalIgnoreCase);

    public bool IsAccountLocked =>
        string.Equals(Reason, "ACCOUNT_LOCKED", StringComparison.OrdinalIgnoreCase);

    public bool PreventsRelogin => IsAccountDisabled || IsAccountLocked;

    public bool IsSimpleNotice => IsAdminKickout || IsAccountDisabled;

    public string GetHeadline()
    {
        if (IsAccountDisabled)
        {
            return "账号已被禁用";
        }

        if (IsAccountLocked)
        {
            return "账号已被锁定";
        }

        return "账号已下线";
    }

    public string GetConfirmButtonText() =>
        PreventsRelogin ? "我知道了" : "我知道了，重新登录";

    public IReadOnlyList<string> BuildUserFriendlyDetails()
    {
        if (IsSimpleNotice)
        {
            return [];
        }

        if (IsAccountLocked)
        {
            var lockedUntilText = FormatLockedUntilDisplay();
            return string.IsNullOrWhiteSpace(lockedUntilText)
                ? []
                : [$"解锁时间：{lockedUntilText}"];
        }

        var details = new List<string>();
        if (MaxActiveDevices is > 0)
        {
            details.Add($"设备上限：{MaxActiveDevices.Value} 台");
        }

        var device = NewLoginDevice;
        if (device is null)
        {
            return details;
        }

        AddDetailIfPresent(details, "新登录设备", device.DeviceName);
        AddDetailIfPresent(details, "客户端", FormatClientType(device.ClientType));
        AddDetailIfPresent(details, "操作系统", FormatOperatingSystem(device));
        AddDetailIfPresent(details, "IP 地址", device.IpAddress);
        AddDetailIfPresent(details, "地理位置", FormatLocation(device));
        AddDetailIfPresent(details, "运营商", device.Isp);
        return details;
    }

    private static void AddDetailIfPresent(List<string> details, string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || string.Equals(value.Trim(), "未知", StringComparison.Ordinal))
        {
            return;
        }

        details.Add($"{label}：{value.Trim()}");
    }

    private string? FormatLockedUntilDisplay()
    {
        if (string.IsNullOrWhiteSpace(LockedUntil))
        {
            return null;
        }

        var text = LockedUntil.Trim();
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var offset))
        {
            return offset.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        }

        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var localTime))
        {
            return localTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        }

        return text;
    }

    public static string FormatOperatingSystem(ForceLogoutDeviceInfo? device)
    {
        if (device is null)
        {
            return "未知";
        }

        var osName = device.OsName?.Trim();
        var osVersion = device.OsVersion?.Trim();
        if (!string.IsNullOrWhiteSpace(osName) && !string.IsNullOrWhiteSpace(osVersion))
        {
            return $"{osName} {osVersion}";
        }

        if (!string.IsNullOrWhiteSpace(osName))
        {
            return osName;
        }

        if (!string.IsNullOrWhiteSpace(osVersion))
        {
            return osVersion;
        }

        return "未知";
    }

    public static string FormatLocation(ForceLogoutDeviceInfo? device)
    {
        if (device is null)
        {
            return "未知";
        }

        var parts = new[] { device.Country, device.Region, device.City }
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Select(part => part!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return parts.Count == 0 ? "未知" : string.Join(" · ", parts);
    }

    public static string FormatValue(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "未知" : value.Trim();

    public static string FormatReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return "未知";
        }

        return reason.Trim() switch
        {
            "DEVICE_LIMIT_EXCEEDED" => "设备数量超限",
            "ADMIN_KICKOUT" => "管理员强制下线",
            "ACCOUNT_DISABLED" => "账号已被禁用",
            "ACCOUNT_LOCKED" => "账号已被锁定",
            _ => reason.Trim()
        };
    }

    public static string FormatMaxActiveDevices(int? maxActiveDevices) =>
        maxActiveDevices is null or <= 0 ? "未知" : $"{maxActiveDevices.Value} 台";

    public static string FormatClientType(string? clientType)
    {
        if (string.IsNullOrWhiteSpace(clientType))
        {
            return "未知";
        }

        return clientType.Trim().ToUpperInvariant() switch
        {
            "WINDOWS" => "Windows",
            "ANDROID" => "Android",
            "IOS" => "iOS",
            "WEB" => "Web",
            _ => clientType.Trim()
        };
    }
}

public sealed class TokenExpiredPushMessage
{
    public string Type { get; set; } = "";
    public string? Message { get; set; }
    public long Timestamp { get; set; }

    public string GetDisplayMessage() =>
        string.IsNullOrWhiteSpace(Message) ? "登录状态已过期，请重新登录。" : Message.Trim();
}

public sealed class HeartbeatTimeoutPushMessage
{
    public string Type { get; set; } = "";
    public string? Message { get; set; }
    public long Timestamp { get; set; }

    public string GetDisplayMessage() =>
        string.IsNullOrWhiteSpace(Message) ? "连接心跳超时，请重新连接" : Message.Trim();
}

public sealed class BroadcastPushMessage
{
    public string Type { get; set; } = "";
    public int MessageId { get; set; }
    public string Title { get; set; } = "";
    public string Content { get; set; } = "";
    public string Level { get; set; } = "";
    public long Timestamp { get; set; }
}

public sealed class WebSocketChatMessage
{
    public string Type { get; set; } = "";
    public int MessageId { get; set; }
    public int ConversationId { get; set; }
    public string SenderType { get; set; } = "";
    public int SenderId { get; set; }
    public string? MessageType { get; set; }
    public string? AttachmentType { get; set; }
    public string? Content { get; set; }
    public string? FileUrl { get; set; }
    public string? FileObjectKey { get; set; }
    public string? FileSha256 { get; set; }
    public string? FileName { get; set; }
    public long? FileSize { get; set; }
    public string? FileContentType { get; set; }
    public string CreatedAt { get; set; } = "";
    public long Timestamp { get; set; }

    public ChatMessage ToChatMessage() => new()
    {
        Id = MessageId,
        ConversationId = ConversationId,
        SenderType = SenderType,
        SenderId = SenderId,
        MessageType = MessageType,
        AttachmentType = AttachmentType,
        Content = Content,
        FileUrl = FileUrl,
        FileObjectKey = FileObjectKey,
        FileSha256 = FileSha256,
        FileName = FileName,
        FileSize = FileSize,
        FileContentType = FileContentType,
        CreatedAt = CreatedAt
    };
}
