namespace NaiwaProxy.Models;

public sealed class AuthSession
{
    public int UserId { get; set; }
    public string Email { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public DateTime AccessTokenExpiresAtUtc { get; set; }
    public List<ServerSubscription> Subscriptions { get; set; } = [];

    public bool IsExpired =>
        string.IsNullOrWhiteSpace(AccessToken) ||
        string.IsNullOrWhiteSpace(RefreshToken) ||
        AccessTokenExpiresAtUtc <= DateTime.UtcNow;
}
