using NaiwaProxy.Models;

namespace NaiwaProxy.Services;

public sealed class NotificationApiService
{
    private readonly ApiClient _apiClient;
    private readonly Func<string> _getBaseUrl;

    public NotificationApiService(ApiClient apiClient, Func<string> getBaseUrl)
    {
        _apiClient = apiClient;
        _getBaseUrl = getBaseUrl;
    }

    public Task<ApiResult<List<NotificationItem>>> ListAsync(CancellationToken cancellationToken = default) =>
        _apiClient.GetAsync<List<NotificationItem>>(_getBaseUrl(), "notifications", cancellationToken: cancellationToken);

    public Task<ApiResult<object?>> MarkReadAsync(int notificationId, CancellationToken cancellationToken = default) =>
        _apiClient.PostAsync<object?>(_getBaseUrl(), $"notifications/{notificationId}/read", body: null, cancellationToken: cancellationToken);
}
