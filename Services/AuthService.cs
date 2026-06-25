using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using NaiwaProxy.Models;

namespace NaiwaProxy.Services;

public sealed partial class AuthService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly TimeSpan AccessTokenLifetime = TimeSpan.FromHours(2);

    private readonly string _sessionPath;
    private readonly ApiClient _apiClient;
    private readonly SubscriptionApiService _subscriptionApi;
    private readonly SubscriptionSyncService _subscriptionSync;
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
            () => RefreshTokenInternalAsync(CancellationToken.None));
        _subscriptionApi = new SubscriptionApiService(_apiClient, () => _apiBaseUrl);
        _subscriptionSync = new SubscriptionSyncService(this, _subscriptionApi);
    }

    public SubscriptionApiService SubscriptionApi => _subscriptionApi;
    public SubscriptionSyncService SubscriptionSync => _subscriptionSync;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiBaseUrl);

    public bool IsAuthenticated => _session is not null &&
                                   !string.IsNullOrWhiteSpace(_session.AccessToken) &&
                                   !string.IsNullOrWhiteSpace(_session.RefreshToken);

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

        if (_session.AccessTokenExpiresAtUtc <= DateTime.UtcNow.AddMinutes(1))
        {
            var refreshed = await RefreshTokenInternalAsync(cancellationToken);
            if (!refreshed)
            {
                return null;
            }
        }

        return _session.AccessToken;
    }

    private async Task<bool> RefreshTokenInternalAsync(CancellationToken cancellationToken = default)
    {
        if (_session is null || string.IsNullOrWhiteSpace(_session.RefreshToken) || !IsConfigured)
        {
            return false;
        }

        try
        {
            var result = await _apiClient.PostPublicAsync<TokenData>(
                _apiBaseUrl,
                "auth/token/refresh",
                new { refreshToken = _session.RefreshToken },
                cancellationToken);

            if (!result.IsSuccess || result.Data is null)
            {
                ClearSession();
                return false;
            }

            _session.AccessToken = result.Data.AccessToken;
            _session.RefreshToken = result.Data.RefreshToken;
            _session.AccessTokenExpiresAtUtc = DateTime.UtcNow.Add(AccessTokenLifetime);
            SaveSession(_session);
            return true;
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Warning($"Token refresh failed: {ex.Message}");
            ClearSession();
            return false;
        }
    }

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
            return JsonSerializer.Deserialize<AuthSession>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Warning($"Failed to load auth session: {ex.Message}");
            return null;
        }
    }

    private void SaveSession(AuthSession session)
    {
        var json = JsonSerializer.Serialize(session, JsonOptions);
        File.WriteAllText(_sessionPath, json);
    }
}
