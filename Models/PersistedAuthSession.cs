namespace NaiwaProxy.Models;

public sealed class PersistedAuthSession
{
    public const int CurrentFormatVersion = 3;

    public int FormatVersion { get; set; } = CurrentFormatVersion;
    public int UserId { get; set; }
    public string Email { get; set; } = "";
    public string? Nickname { get; set; }
    public string? AvatarUrl { get; set; }
    public string ProtectedAccessToken { get; set; } = "";
    public string ProtectedRefreshToken { get; set; } = "";
    public DateTime AccessTokenExpiresAtUtc { get; set; }
    public List<ServerSubscription> Subscriptions { get; set; } = [];
}
