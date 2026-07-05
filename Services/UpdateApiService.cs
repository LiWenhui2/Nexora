using NaiwaProxy.Models;

namespace NaiwaProxy.Services;

public sealed class UpdateApiService
{
    private readonly ApiClient _apiClient;
    private readonly Func<string> _getBaseUrl;

    public UpdateApiService(ApiClient apiClient, Func<string> getBaseUrl)
    {
        _apiClient = apiClient;
        _getBaseUrl = getBaseUrl;
    }

    public Task<ApiResult<AppUpdateRelease?>> GetLatestAsync(string platform = "WINDOWS", CancellationToken cancellationToken = default) =>
        _apiClient.GetAsync<AppUpdateRelease?>(_getBaseUrl(), $"updates/latest?platform={Uri.EscapeDataString(platform)}", cancellationToken: cancellationToken);
}
