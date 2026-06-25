using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using NaiwaProxy.Models;

namespace NaiwaProxy.Services;

public sealed class ApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly HttpClient _http;
    private readonly Func<Task<string?>> _getAccessTokenAsync;
    private readonly Func<Task<bool>> _refreshTokenAsync;

    public ApiClient(Func<Task<string?>> getAccessTokenAsync, Func<Task<bool>> refreshTokenAsync)
    {
        _getAccessTokenAsync = getAccessTokenAsync;
        _refreshTokenAsync = refreshTokenAsync;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    }

    public async Task<ApiResult<T>> GetAsync<T>(string baseUrl, string path, bool requireAuth = true, CancellationToken cancellationToken = default)
    {
        return await SendAsync<T>(HttpMethod.Get, baseUrl, path, body: null, requireAuth, cancellationToken);
    }

    public async Task<ApiResult<T>> PostAsync<T>(string baseUrl, string path, object? body, bool requireAuth = true, CancellationToken cancellationToken = default)
    {
        return await SendAsync<T>(HttpMethod.Post, baseUrl, path, body, requireAuth, cancellationToken);
    }

    public async Task<ApiResult<T>> PatchAsync<T>(string baseUrl, string path, object body, bool requireAuth = true, CancellationToken cancellationToken = default)
    {
        return await SendAsync<T>(HttpMethod.Patch, baseUrl, path, body, requireAuth, cancellationToken);
    }

    public async Task<ApiResult<T>> DeleteAsync<T>(string baseUrl, string path, bool requireAuth = true, CancellationToken cancellationToken = default)
    {
        return await SendAsync<T>(HttpMethod.Delete, baseUrl, path, body: null, requireAuth, cancellationToken);
    }

    private async Task<ApiResult<T>> SendAsync<T>(
        HttpMethod method,
        string baseUrl,
        string path,
        object? body,
        bool requireAuth,
        CancellationToken cancellationToken)
    {
        var result = await SendCoreAsync<T>(method, baseUrl, path, body, requireAuth, cancellationToken);
        if (requireAuth && result.Code == 401)
        {
            if (await _refreshTokenAsync())
            {
                return await SendCoreAsync<T>(method, baseUrl, path, body, requireAuth, cancellationToken);
            }
        }

        return result;
    }

    private async Task<ApiResult<T>> SendCoreAsync<T>(
        HttpMethod method,
        string baseUrl,
        string path,
        object? body,
        bool requireAuth,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, $"{baseUrl.TrimEnd('/')}/{path.TrimStart('/')}");
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }

        if (requireAuth)
        {
            var token = await _getAccessTokenAsync();
            if (!string.IsNullOrWhiteSpace(token))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
        }

        using var response = await _http.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseResponse<T>(text);
    }

    internal static ApiResult<T> ParseResponse<T>(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new ApiResult<T> { Code = 500, Message = "服务端返回空响应。" };
        }

        try
        {
            var result = JsonSerializer.Deserialize<ApiResult<T>>(text, JsonOptions);
            return result ?? new ApiResult<T> { Code = 500, Message = "无法解析服务端响应。" };
        }
        catch (JsonException)
        {
            return new ApiResult<T> { Code = 500, Message = text.Trim() };
        }
    }

    internal async Task<ApiResult<T>> PostPublicAsync<T>(string baseUrl, string path, object body, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/{path.TrimStart('/')}")
        {
            Content = JsonContent.Create(body, options: JsonOptions)
        };

        using var response = await _http.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseResponse<T>(text);
    }
}
