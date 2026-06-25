using NaiwaProxy.Models;

namespace NaiwaProxy.Services;

public sealed class SubscriptionApiService
{
    private readonly ApiClient _apiClient;
    private readonly Func<string> _getBaseUrl;

    public SubscriptionApiService(ApiClient apiClient, Func<string> getBaseUrl)
    {
        _apiClient = apiClient;
        _getBaseUrl = getBaseUrl;
    }

    public Task<ApiResult<List<ServerSubscription>>> ListAsync(CancellationToken cancellationToken = default) =>
        _apiClient.GetAsync<List<ServerSubscription>>(_getBaseUrl(), "subscription", cancellationToken: cancellationToken);

    public Task<ApiResult<object?>> CreateAsync(CreateSubscriptionRequest request, CancellationToken cancellationToken = default) =>
        _apiClient.PostAsync<object?>(
            _getBaseUrl(),
            "subscription",
            new
            {
                name = request.Name,
                url = request.Url,
                total_bytes = request.TotalBytes,
                remain_bytes = request.RemainBytes,
                expireAt = request.ExpireAt
            },
            cancellationToken: cancellationToken);

    public Task<ApiResult<object?>> UpdateAsync(int subscriptionId, UpdateSubscriptionRequest request, CancellationToken cancellationToken = default) =>
        _apiClient.PatchAsync<object?>(
            _getBaseUrl(),
            $"subscription/{subscriptionId}",
            new
            {
                name = request.Name,
                totalBytes = request.TotalBytes,
                remainBytes = request.RemainBytes,
                expireAt = request.ExpireAt
            },
            cancellationToken: cancellationToken);

    public Task<ApiResult<object?>> DeleteAsync(int subscriptionId, CancellationToken cancellationToken = default) =>
        _apiClient.DeleteAsync<object?>(_getBaseUrl(), $"subscription/{subscriptionId}", cancellationToken: cancellationToken);

    public async Task<ServerSubscription?> FindByUrlAsync(string url, CancellationToken cancellationToken = default)
    {
        var result = await ListAsync(cancellationToken);
        if (!result.IsSuccess || result.Data is null)
        {
            return null;
        }

        return result.Data.FirstOrDefault(item => string.Equals(item.Url, url, StringComparison.OrdinalIgnoreCase));
    }
}
