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
    public string? Avatar { get; set; }
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
