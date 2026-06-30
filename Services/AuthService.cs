using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using NaiwaProxy.Models;

namespace NaiwaProxy.Services;

public sealed partial class AuthService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly TimeSpan AccessTokenLifetime = TimeSpan.FromHours(2);
    private static readonly TimeSpan AccessTokenRefreshSkew = TimeSpan.FromMinutes(1);

    private readonly string _sessionPath;
    private readonly ApiClient _apiClient;
    private readonly SubscriptionApiService _subscriptionApi;
    private readonly SubscriptionSyncService _subscriptionSync;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private string _apiBaseUrl = "";
    private AuthSession? _session;

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.CultureInvariant)]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"[A-Za-z]")]
    private static partial Regex PasswordLetterRegex();

    [GeneratedRegex(@"\d")]
    private static partial Regex PasswordDigitRegex();

    public event Action? AuthStateChanged;

    public AuthService()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Nexora");
        Directory.CreateDirectory(directory);
        _sessionPath = Path.Combine(directory, "auth-session.json");
        _session = LoadSession();
        _apiClient = new ApiClient(
            () => GetAccessTokenAsync(CancellationToken.None),
            () => RefreshTokenInternalAsync(CancellationToken.None, forceRefresh: true));
        _subscriptionApi = new SubscriptionApiService(_apiClient, () => _apiBaseUrl);
        _subscriptionSync = new SubscriptionSyncService(this, _subscriptionApi);
    }

    public SubscriptionApiService SubscriptionApi => _subscriptionApi;
    public SubscriptionSyncService SubscriptionSync => _subscriptionSync;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiBaseUrl);

    public bool IsAuthenticated =>
        _session is not null &&
        !string.IsNullOrWhiteSpace(_session.RefreshToken) &&
        !string.IsNullOrWhiteSpace(_session.AccessToken) &&
        !IsAccessTokenExpired(_session);

    public AuthSession? CurrentSession => _session;

    public string? CurrentEmail => _session?.Email;

    public void Configure(string apiBaseUrl)
    {
        _apiBaseUrl = apiBaseUrl.Trim().TrimEnd('/');
    }

    public static bool IsValidEmail(string email) =>
        !string.IsNullOrWhiteSpace(email) && EmailRegex().IsMatch(email.Trim());

    public static bool IsValidPassword(string password) =>
        !string.IsNullOrWhiteSpace(password) &&
        password.Length >= 6 &&
        PasswordLetterRegex().IsMatch(password) &&
        PasswordDigitRegex().IsMatch(password);

    public async Task<bool> TryRestoreSessionAsync(CancellationToken cancellationToken = default)
    {
        if (_session is null || string.IsNullOrWhiteSpace(_session.RefreshToken))
        {
            return false;
        }

        if (!IsAccessTokenExpired(_session))
        {
            return true;
        }

        return await RefreshTokenInternalAsync(cancellationToken);
    }

    public Task<bool> RefreshSessionAsync(CancellationToken cancellationToken = default) =>
        RefreshTokenInternalAsync(cancellationToken, forceRefresh: true);

    public async Task<AuthResult> SendRegisterCodeAsync(string email, CancellationToken cancellationToken = default)
    {
        if (!IsValidEmail(email))
        {
            return AuthResult.Fail("请输入有效的邮箱地址。");
        }

        if (!IsConfigured)
        {
            return AuthResult.Fail("认证服务尚未配置，暂时无法发送验证码。");
        }

        try
        {
            var result = await _apiClient.PostPublicAsync<object?>(
                _apiBaseUrl,
                "auth/email/code",
                new { email = email.Trim(), scene = "register" },
                cancellationToken);

            return result.IsSuccess
                ? AuthResult.Ok(new AuthSession(), result.Message)
                : AuthResult.Fail(result.Message);
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Error("Failed to send register verification code.", ex);
            return AuthResult.Fail($"验证码发送失败：{ex.Message}");
        }
    }

    public async Task<AuthResult> RegisterAsync(
        string email,
        string password,
        string confirmPassword,
        string code,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidEmail(email))
        {
            return AuthResult.Fail("请输入有效的邮箱地址。");
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            return AuthResult.Fail("请输入邮箱验证码。");
        }

        if (!IsValidPassword(password))
        {
            return AuthResult.Fail("密码至少 6 位，且必须同时包含字母和数字。");
        }

        if (!string.Equals(password, confirmPassword, StringComparison.Ordinal))
        {
            return AuthResult.Fail("两次输入的密码不一致。");
        }

        if (!IsConfigured)
        {
            return AuthResult.Fail("认证服务尚未配置，暂时无法注册。");
        }

        try
        {
            var result = await _apiClient.PostPublicAsync<object?>(
                _apiBaseUrl,
                "auth/register",
                new
                {
                    email = email.Trim(),
                    password,
                    code = code.Trim()
                },
                cancellationToken);

            if (!result.IsSuccess)
            {
                return AuthResult.Fail(result.Message);
            }

            return await LoginAsync(email, password, cancellationToken);
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Error("Registration failed.", ex);
            return AuthResult.Fail($"注册失败：{ex.Message}");
        }
    }

    public async Task<AuthResult> LoginAsync(
        string email,
        string password,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidEmail(email))
        {
            return AuthResult.Fail("请输入有效的邮箱地址。");
        }

        if (string.IsNullOrWhiteSpace(password))
        {
            return AuthResult.Fail("请输入密码。");
        }

        if (!IsConfigured)
        {
            return AuthResult.Fail("认证服务尚未配置，暂时无法登录。");
        }

        try
        {
            var result = await _apiClient.PostPublicAsync<LoginData>(
                _apiBaseUrl,
                "auth/login/password",
                new
                {
                    email = email.Trim(),
                    password
                },
                cancellationToken);

            if (!result.IsSuccess || result.Data is null)
            {
                return AuthResult.Fail(result.Message);
            }

            var session = CreateSession(result.Data);
            ApplySession(session);
            return AuthResult.Ok(session, result.Message);
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Error("Login failed.", ex);
            return AuthResult.Fail($"登录失败：{ex.Message}");
        }
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        var refreshToken = _session?.RefreshToken;
        if (!string.IsNullOrWhiteSpace(refreshToken) && IsConfigured)
        {
            try
            {
                await _apiClient.PostPublicAsync<object?>(
                    _apiBaseUrl,
                    "auth/logout",
                    new { refreshToken },
                    cancellationToken);
            }
            catch (Exception ex)
            {
                DiagnosticLogService.Warning($"Logout request failed: {ex.Message}");
            }
        }

        ClearSession();
    }

    public async Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        if (_session is null)
        {
            return null;
        }

        if (IsAccessTokenExpired(_session))
        {
            var refreshed = await RefreshTokenInternalAsync(cancellationToken);
            if (!refreshed)
            {
                return null;
            }
        }

        return _session.AccessToken;
    }

    private async Task<bool> RefreshTokenInternalAsync(
        CancellationToken cancellationToken = default,
        bool forceRefresh = false)
    {
        if (_session is null || string.IsNullOrWhiteSpace(_session.RefreshToken) || !IsConfigured)
        {
            return false;
        }

        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            if (_session is null || string.IsNullOrWhiteSpace(_session.RefreshToken))
            {
                return false;
            }

            if (!forceRefresh && !IsAccessTokenExpired(_session))
            {
                return true;
            }

            var result = await _apiClient.PostPublicAsync<TokenData>(
                _apiBaseUrl,
                "auth/token/refresh",
                new { refreshToken = _session.RefreshToken },
                cancellationToken);

            if (!result.IsSuccess || result.Data is null)
            {
                if (ShouldClearSessionAfterRefreshFailure(result.Code))
                {
                    ClearSession();
                }
                else
                {
                    DiagnosticLogService.Warning($"Token refresh failed without clearing saved session: {result.Message}");
                }

                return false;
            }

            _session.AccessToken = result.Data.AccessToken;
            _session.RefreshToken = result.Data.RefreshToken;
            _session.AccessTokenExpiresAtUtc = DateTime.UtcNow.Add(AccessTokenLifetime);
            SaveSession(_session);
            AuthStateChanged?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Warning($"Token refresh failed without clearing saved session: {ex.Message}");
            return false;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private static bool IsAccessTokenExpired(AuthSession session) =>
        session.AccessTokenExpiresAtUtc <= DateTime.UtcNow.Add(AccessTokenRefreshSkew);

    private static bool ShouldClearSessionAfterRefreshFailure(int code) =>
        code is 400 or 401 or 403;

    private static AuthSession CreateSession(LoginData data) =>
        new()
        {
            UserId = data.UserInfo.Id,
            Email = data.UserInfo.Email,
            AccessToken = data.AccessToken,
            RefreshToken = data.RefreshToken,
            AccessTokenExpiresAtUtc = DateTime.UtcNow.Add(AccessTokenLifetime),
            Subscriptions = data.Subscriptions
        };

    public void RemoveSubscriptionFromSession(int subscriptionId)
    {
        if (_session is null)
        {
            return;
        }

        _session.Subscriptions.RemoveAll(subscription => subscription.Id == subscriptionId);
        SaveSession(_session);
    }

    public void AddOrUpdateSubscriptionInSession(ServerSubscription subscription)
    {
        if (_session is null || subscription.Id <= 0)
        {
            return;
        }

        var existing = _session.Subscriptions.FirstOrDefault(item => item.Id == subscription.Id);
        if (existing is null)
        {
            existing = _session.Subscriptions.FirstOrDefault(item =>
                string.Equals(item.Url, subscription.Url, StringComparison.OrdinalIgnoreCase));
        }

        if (existing is null)
        {
            _session.Subscriptions.Add(subscription);
        }
        else
        {
            existing.Id = subscription.Id;
            existing.Name = subscription.Name;
            existing.Url = subscription.Url;
        }

        SaveSession(_session);
    }

    public void ReplaceSessionSubscriptions(IReadOnlyList<ServerSubscription> subscriptions)
    {
        if (_session is null)
        {
            return;
        }

        _session.Subscriptions = subscriptions.ToList();
        SaveSession(_session);
    }

    private void ApplySession(AuthSession session)
    {
        _session = session;
        SaveSession(session);
        AuthStateChanged?.Invoke();
    }

    private void ClearSession()
    {
        _session = null;
        if (File.Exists(_sessionPath))
        {
            File.Delete(_sessionPath);
        }

        AuthStateChanged?.Invoke();
    }

    private AuthSession? LoadSession()
    {
        if (!File.Exists(_sessionPath))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(_sessionPath);
            var persisted = JsonSerializer.Deserialize<PersistedAuthSession>(json, JsonOptions);
            if (persisted is not null && persisted.FormatVersion >= PersistedAuthSession.CurrentFormatVersion)
            {
                return FromPersistedSession(persisted);
            }

            var legacy = JsonSerializer.Deserialize<AuthSession>(json, JsonOptions);
            if (legacy is null)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(legacy.AccessToken) || !string.IsNullOrWhiteSpace(legacy.RefreshToken))
            {
                SaveSession(legacy);
            }

            return legacy;
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Warning($"Failed to load auth session: {ex.Message}");
            return null;
        }
    }

    private void SaveSession(AuthSession session)
    {
        var persisted = ToPersistedSession(session);
        var json = JsonSerializer.Serialize(persisted, JsonOptions);
        var tempPath = $"{_sessionPath}.tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, _sessionPath, overwrite: true);
    }

    private static AuthSession FromPersistedSession(PersistedAuthSession persisted) =>
        new()
        {
            UserId = persisted.UserId,
            Email = persisted.Email,
            AccessToken = SessionTokenProtection.Unprotect(persisted.ProtectedAccessToken),
            RefreshToken = SessionTokenProtection.Unprotect(persisted.ProtectedRefreshToken),
            AccessTokenExpiresAtUtc = persisted.AccessTokenExpiresAtUtc,
            Subscriptions = persisted.Subscriptions
        };

    private static PersistedAuthSession ToPersistedSession(AuthSession session) =>
        new()
        {
            FormatVersion = PersistedAuthSession.CurrentFormatVersion,
            UserId = session.UserId,
            Email = session.Email,
            ProtectedAccessToken = SessionTokenProtection.Protect(session.AccessToken),
            ProtectedRefreshToken = SessionTokenProtection.Protect(session.RefreshToken),
            AccessTokenExpiresAtUtc = session.AccessTokenExpiresAtUtc,
            Subscriptions = session.Subscriptions
        };
}
