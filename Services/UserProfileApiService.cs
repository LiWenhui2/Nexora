using NaiwaProxy.Models;

namespace NaiwaProxy.Services;

public sealed class UserProfileApiService
{
    private readonly ApiClient _apiClient;
    private readonly Func<string> _getBaseUrl;

    public UserProfileApiService(ApiClient apiClient, Func<string> getBaseUrl)
    {
        _apiClient = apiClient;
        _getBaseUrl = getBaseUrl;
    }

    public Task<ApiResult<UserProfile>> GetProfileAsync(CancellationToken cancellationToken = default) =>
        _apiClient.GetAsync<UserProfile>(_getBaseUrl(), "user/profile", cancellationToken: cancellationToken);

    public Task<ApiResult<UserProfile>> UpdateNicknameAsync(string nickname, CancellationToken cancellationToken = default) =>
        _apiClient.PatchAsync<UserProfile>(
            _getBaseUrl(),
            "user/profile/nickname",
            new UpdateNicknameRequest { Nickname = nickname },
            cancellationToken: cancellationToken);

    public Task<ApiResult<UserProfile>> UploadAvatarAsync(string filePath, CancellationToken cancellationToken = default) =>
        _apiClient.PostMultipartAsync<UserProfile>(
            _getBaseUrl(),
            "user/profile/avatar",
            "file",
            filePath,
            cancellationToken: cancellationToken);
}
