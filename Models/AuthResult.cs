namespace NaiwaProxy.Models;

public sealed class AuthResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    public AuthSession? Session { get; init; }

    public static AuthResult Ok(AuthSession session, string message = "登录成功。") =>
        new() { Success = true, Message = message, Session = session };

    public static AuthResult Fail(string message) =>
        new() { Success = false, Message = message };
}
