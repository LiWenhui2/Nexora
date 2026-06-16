namespace NaiwaProxy.Models;

public sealed class AuthSession
{
    public string Email { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public DateTime ExpiresAtUtc { get; set; }

    public bool IsExpired => string.IsNullOrWhiteSpace(AccessToken) ||
                             ExpiresAtUtc <= DateTime.UtcNow;
}
