using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using NaiwaProxy.Models;

namespace NaiwaProxy.Services;

public sealed partial class AuthService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly string _sessionPath;
    private string _apiBaseUrl = "";
    private AuthSession? _session;

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.CultureInvariant)]
    private static partial Regex EmailRegex();

    public event Action? AuthStateChanged;

    public AuthService()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Nexora");
        Directory.CreateDirectory(directory);
        _sessionPath = Path.Combine(directory, "auth-session.json");
        _session = LoadSession();
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiBaseUrl);

    public bool IsAuthenticated => _session is { IsExpired: false };

    public AuthSession? CurrentSession => _session is { IsExpired: false } ? _session : null;

    public string? CurrentEmail => CurrentSession?.Email;

    public void Configure(string apiBaseUrl)
    {
        _apiBaseUrl = apiBaseUrl.Trim().TrimEnd('/');
    }

    public static bool IsValidEmail(string email) =>
        !string.IsNullOrWhiteSpace(email) && EmailRegex().IsMatch(email.Trim());

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
            using var response = await Http.PostAsJsonAsync(
                $"{_apiBaseUrl}/api/auth/email/code",
                new { email = email.Trim(), scene = "register" },
                cancellationToken);

            var payload = await ReadResponseAsync(response, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return AuthResult.Fail(payload.Message ?? "验证码发送失败。");
            }

            return AuthResult.Ok(new AuthSession(), payload.Message ?? "验证码已发送，请查收邮箱。");
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

        if (string.IsNullOrWhiteSpace(password) || password.Length < 8)
        {
            return AuthResult.Fail("密码长度至少 8 位。");
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
            using var response = await Http.PostAsJsonAsync(
                $"{_apiBaseUrl}/api/auth/email/register",
                new
                {
                    email = email.Trim(),
                    password,
                    code = code.Trim()
                },
                cancellationToken);

            var payload = await ReadResponseAsync(response, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return AuthResult.Fail(payload.Message ?? "注册失败。");
            }

            if (payload.Session is null)
            {
                return AuthResult.Fail("注册成功，但服务端未返回登录凭证。");
            }

            ApplySession(payload.Session);
            return AuthResult.Ok(payload.Session, payload.Message ?? "注册成功。");
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
            using var response = await Http.PostAsJsonAsync(
                $"{_apiBaseUrl}/api/auth/email/login",
                new
                {
                    email = email.Trim(),
                    password
                },
                cancellationToken);

            var payload = await ReadResponseAsync(response, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return AuthResult.Fail(payload.Message ?? "登录失败。");
            }

            if (payload.Session is null)
            {
                return AuthResult.Fail("登录失败，服务端未返回有效凭证。");
            }

            ApplySession(payload.Session);
            return AuthResult.Ok(payload.Session, payload.Message ?? "登录成功。");
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Error("Login failed.", ex);
            return AuthResult.Fail($"登录失败：{ex.Message}");
        }
    }

    public void Logout()
    {
        _session = null;
        if (File.Exists(_sessionPath))
        {
            File.Delete(_sessionPath);
        }

        AuthStateChanged?.Invoke();
    }

    private void ApplySession(AuthSession session)
    {
        _session = session;
        SaveSession(session);
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

    private static async Task<ApiPayload> ReadResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(body))
        {
            return new ApiPayload();
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var message = ReadString(root, "message") ?? ReadString(root, "error");
            var session = TryReadSession(root);
            return new ApiPayload(message, session);
        }
        catch (JsonException)
        {
            return new ApiPayload(body.Trim());
        }
    }

    private static AuthSession? TryReadSession(JsonElement root)
    {
        var token = ReadString(root, "accessToken") ?? ReadString(root, "token");
        var email = ReadString(root, "email");
        var expiresAt = ReadDateTime(root, "expiresAt") ?? ReadDateTime(root, "expiresAtUtc");

        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(email))
        {
            if (root.TryGetProperty("data", out var data))
            {
                token ??= ReadString(data, "accessToken") ?? ReadString(data, "token");
                email ??= ReadString(data, "email");
                expiresAt ??= ReadDateTime(data, "expiresAt") ?? ReadDateTime(data, "expiresAtUtc");
            }
        }

        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(email))
        {
            return null;
        }

        return new AuthSession
        {
            Email = email,
            AccessToken = token,
            ExpiresAtUtc = expiresAt ?? DateTime.UtcNow.AddDays(7)
        };
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }

    private static DateTime? ReadDateTime(JsonElement element, string propertyName)
    {
        var text = ReadString(element, propertyName);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return DateTime.TryParse(text, out var parsed) ? parsed.ToUniversalTime() : null;
    }

    private sealed record ApiPayload(string? Message = null, AuthSession? Session = null);
}
