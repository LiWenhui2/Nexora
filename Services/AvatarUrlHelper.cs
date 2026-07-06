namespace NaiwaProxy.Services;

public static class AvatarUrlHelper
{
    public const string DefaultUserAvatarUrl = "https://nexora.limoon.cn/avatars/default.jpg";
    public const string AdminAvatarUrl = "https://nexora.limoon.cn/admin/avatar/duodong.jpg";

    public static string ResolveUserAvatarUrl(string? avatarUrl) =>
        string.IsNullOrWhiteSpace(avatarUrl) ? DefaultUserAvatarUrl : avatarUrl.Trim();

    public static string ResolveChatAvatarUrl(bool isAdmin, string? userAvatarUrl) =>
        isAdmin ? AdminAvatarUrl : ResolveUserAvatarUrl(userAvatarUrl);
}
