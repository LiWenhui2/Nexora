using NaiwaProxy.Models;

namespace NaiwaProxy.Services;

public sealed class ChatApiService
{
    private readonly ApiClient _apiClient;
    private readonly Func<string> _getBaseUrl;

    public ChatApiService(ApiClient apiClient, Func<string> getBaseUrl)
    {
        _apiClient = apiClient;
        _getBaseUrl = getBaseUrl;
    }

    public Task<ApiResult<List<ChatMessage>>> ListMessagesAsync(CancellationToken cancellationToken = default) =>
        _apiClient.GetAsync<List<ChatMessage>>(_getBaseUrl(), "chats/messages", cancellationToken: cancellationToken);

    public Task<ApiResult<ChatMessage>> SendMessageAsync(string content, CancellationToken cancellationToken = default) =>
        _apiClient.PostAsync<ChatMessage>(
            _getBaseUrl(),
            "chats/messages",
            new SendChatMessageRequest { Content = content },
            cancellationToken: cancellationToken);
}
