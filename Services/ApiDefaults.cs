namespace NaiwaProxy.Services;

public static class ApiDefaults
{
    public const string BaseUrl = "http://43.136.117.106:8080/api/v1";

    public static string NormalizeAuthApiBaseUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Contains("localhost", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase))
        {
            return BaseUrl;
        }

        var trimmed = value.Trim().TrimEnd('/');
        if (trimmed.EndsWith("/api/v1", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        return $"{trimmed}/api/v1";
    }
}
