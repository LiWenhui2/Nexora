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
                        await DisconnectAsync();
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
            switch (type)
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
            }
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Warning($"Backend WebSocket message parse failed: {ex.Message}");
        }
    }

    private async Task DisconnectCoreAsync()
    {
        if (_lifecycleCts is not null)
        {
            await _lifecycleCts.CancelAsync();
            _lifecycleCts.Dispose();
            _lifecycleCts = null;
        }

        if (_receiveTask is not null)
        {
            try
            {
                await _receiveTask;
            }
            catch
            {
            }

            _receiveTask = null;
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
    public string Content { get; set; } = "";
    public string CreatedAt { get; set; } = "";
    public long Timestamp { get; set; }

    public ChatMessage ToChatMessage() => new()
    {
        Id = MessageId,
        ConversationId = ConversationId,
        SenderType = SenderType,
        SenderId = SenderId,
        Content = Content,
        CreatedAt = CreatedAt
    };
}
