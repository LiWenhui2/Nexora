using System.Text;
using System.Text.Json;
using NaiwaProxy.Models;

namespace NaiwaProxy.Services;

public static class VmessLinkParser
{
    public static VmessProfile Parse(string link)
    {
        if (string.IsNullOrWhiteSpace(link))
        {
            throw new ArgumentException("VMess link is empty.");
        }

        var trimmed = link.Trim();
        if (!trimmed.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException("Only vmess:// links are supported in this build.");
        }

        var payload = trimmed["vmess://".Length..].Trim();
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(NormalizeBase64(payload)));
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var profile = new VmessProfile
        {
            Name = GetString(root, "ps", "VMess Server"),
            Remark = GetString(root, "ps", ""),
            Address = GetString(root, "add", ""),
            Port = GetInt(root, "port", 443),
            UserId = GetString(root, "id", ""),
            AlterId = GetInt(root, "aid", 0),
            Security = NormalizeSecurity(GetString(root, "scy", GetString(root, "security", "auto"))),
            Network = GetString(root, "net", "tcp"),
            Type = GetString(root, "type", "none"),
            Host = GetString(root, "host", ""),
            Path = GetString(root, "path", ""),
            Tls = GetString(root, "tls", ""),
            Sni = GetString(root, "sni", "")
        };

        if (string.IsNullOrWhiteSpace(profile.Address) || string.IsNullOrWhiteSpace(profile.UserId))
        {
            throw new FormatException("VMess link must contain add and id fields.");
        }

        return profile;
    }

    private static string NormalizeBase64(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        var padding = normalized.Length % 4;
        return padding == 0 ? normalized : normalized.PadRight(normalized.Length + 4 - padding, '=');
    }

    private static string GetString(JsonElement root, string name, string fallback)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return fallback;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? fallback,
            JsonValueKind.Number => value.GetRawText(),
            _ => fallback
        };
    }

    private static int GetInt(JsonElement root, string name, int fallback)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return fallback;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            return number;
        }

        return int.TryParse(value.GetString(), out number) ? number : fallback;
    }

    private static string NormalizeSecurity(string security)
    {
        return string.IsNullOrWhiteSpace(security) ? "auto" : security.Trim();
    }
}
