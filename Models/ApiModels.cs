namespace NaiwaProxy.Models;

public sealed class ApiResult<T>
{
    public int Code { get; init; }
    public string Message { get; init; } = "";
    public T? Data { get; init; }

    public bool IsSuccess => Code == 200;
}

public sealed class UserInfo
{
    public int Id { get; set; }
    public string Email { get; set; } = "";
    public string? Nickname { get; set; }
    public string? Avatar { get; set; }
    public string? AvatarUrl { get; set; }

    public string? ResolvedAvatarUrl =>
        !string.IsNullOrWhiteSpace(AvatarUrl) ? AvatarUrl : Avatar;
}

public sealed class UserProfile
{
    public int Id { get; set; }
    public string Email { get; set; } = "";
    public string? Nickname { get; set; }
    public string? AvatarUrl { get; set; }
}

public sealed class UpdateNicknameRequest
{
    public string Nickname { get; set; } = "";
}

public sealed class ServerSubscription
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
}

public sealed class LoginData
{
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public UserInfo UserInfo { get; set; } = new();
    public List<ServerSubscription> Subscriptions { get; set; } = [];
}

public sealed class TokenData
{
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
}

public sealed class CreateSubscriptionRequest
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public long? TotalBytes { get; set; }
    public long? RemainBytes { get; set; }
    public string? ExpireAt { get; set; }
}

public sealed class UpdateSubscriptionRequest
{
    public string? Name { get; set; }
    public long? TotalBytes { get; set; }
    public long? RemainBytes { get; set; }
    public string? ExpireAt { get; set; }
}

public sealed class NotificationItem
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public string Content { get; set; } = "";
    public string Level { get; set; } = "";
    public bool Read { get; set; }
    public string CreatedAt { get; set; } = "";

    public string DisplayBody => Level;

    public string DisplayLevel => Content;
}

public sealed class ChatMessage
{
    public int Id { get; set; }
    public int ConversationId { get; set; }
    public string SenderType { get; set; } = "";
    public int SenderId { get; set; }
    public string Content { get; set; } = "";
    public string CreatedAt { get; set; } = "";

    public bool IsFromAdmin => string.Equals(SenderType, "ADMIN", StringComparison.OrdinalIgnoreCase);
}

public sealed class SendChatMessageRequest
{
    public string Content { get; set; } = "";
}
