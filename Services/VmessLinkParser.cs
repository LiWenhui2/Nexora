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
        if (trimmed.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase))
        {
            return ParseVmess(trimmed);
        }

        if (trimmed.StartsWith("vless://", StringComparison.OrdinalIgnoreCase))
        {
            return ParseVless(trimmed);
        }

        if (trimmed.StartsWith("trojan://", StringComparison.OrdinalIgnoreCase))
        {
            return ParseTrojan(trimmed);
        }

        if (trimmed.StartsWith("ss://", StringComparison.OrdinalIgnoreCase))
        {
            return ParseShadowsocks(trimmed);
        }

        if (trimmed.StartsWith("socks://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("socks5://", StringComparison.OrdinalIgnoreCase))
        {
            return ParseSocks(trimmed);
        }

        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return ParseHttp(trimmed);
        }

        throw new FormatException("仅支持 vmess://、vless://、trojan://、ss://、socks://、http:// 节点链接。");
    }

    private static VmessProfile ParseVmess(string trimmed)
    {
        var payload = trimmed["vmess://".Length..].Trim();
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(NormalizeBase64(payload)));
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var profile = new VmessProfile
        {
            Protocol = "vmess",
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
            throw new FormatException("VMess 链接必须包含 add 和 id 字段。");
        }

        return profile;
    }

    private static VmessProfile ParseVless(string link)
    {
        var uri = CreateUri(link);
        var query = ParseQuery(uri.Query);
        var profile = BuildUriProfile(uri, "vless", "VLESS Server");
        profile.UserId = Uri.UnescapeDataString(uri.UserInfo);
        profile.Security = query.GetValueOrDefault("encryption", "none");
        profile.Network = query.GetValueOrDefault("type", "tcp");
        profile.Tls = NormalizeTls(query.GetValueOrDefault("security", ""));
        profile.Host = query.GetValueOrDefault("host", "");
        profile.Path = query.GetValueOrDefault("path", "");
        profile.Sni = query.GetValueOrDefault("sni", query.GetValueOrDefault("peer", ""));
        profile.Type = query.GetValueOrDefault("headerType", "none");
        if (string.IsNullOrWhiteSpace(profile.UserId))
        {
            throw new FormatException("VLESS 链接必须包含 UUID。");
        }

        return profile;
    }

    private static VmessProfile ParseTrojan(string link)
    {
        var uri = CreateUri(link);
        var query = ParseQuery(uri.Query);
        var profile = BuildUriProfile(uri, "trojan", "Trojan Server");
        profile.Password = Uri.UnescapeDataString(uri.UserInfo);
        profile.Network = query.GetValueOrDefault("type", "tcp");
        profile.Tls = NormalizeTls(query.GetValueOrDefault("security", "tls"));
        profile.Host = query.GetValueOrDefault("host", "");
        profile.Path = query.GetValueOrDefault("path", "");
        profile.Sni = query.GetValueOrDefault("sni", query.GetValueOrDefault("peer", ""));
        if (string.IsNullOrWhiteSpace(profile.Password))
        {
            throw new FormatException("Trojan 链接必须包含密码。");
        }

        return profile;
    }

    private static VmessProfile ParseShadowsocks(string link)
    {
        var body = link["ss://".Length..];
        var fragmentIndex = body.IndexOf('#');
        var name = fragmentIndex >= 0 ? Uri.UnescapeDataString(body[(fragmentIndex + 1)..]) : "Shadowsocks Server";
        body = fragmentIndex >= 0 ? body[..fragmentIndex] : body;
        var atIndex = body.LastIndexOf('@');
        if (atIndex < 0)
        {
            body = Encoding.UTF8.GetString(Convert.FromBase64String(NormalizeBase64(body)));
            atIndex = body.LastIndexOf('@');
        }

        if (atIndex < 0)
        {
            throw new FormatException("Shadowsocks 链接格式无效。");
        }

        var userInfo = body[..atIndex];
        var endpoint = body[(atIndex + 1)..];
        if (!userInfo.Contains(':'))
        {
            userInfo = Encoding.UTF8.GetString(Convert.FromBase64String(NormalizeBase64(userInfo)));
        }

        var separator = userInfo.IndexOf(':');
        if (separator <= 0)
        {
            throw new FormatException("Shadowsocks 链接缺少加密方式或密码。");
        }

        var endpointUri = CreateUri($"ss://placeholder@{endpoint}");
        return new VmessProfile
        {
            Protocol = "shadowsocks",
            Name = string.IsNullOrWhiteSpace(name) ? "Shadowsocks Server" : name,
            Remark = name,
            Address = endpointUri.Host,
            Port = endpointUri.Port,
            Security = Uri.UnescapeDataString(userInfo[..separator]),
            Password = Uri.UnescapeDataString(userInfo[(separator + 1)..]),
            Network = "tcp"
        };
    }

    private static VmessProfile ParseSocks(string link)
    {
        var uri = CreateUri(link.Replace("socks5://", "socks://", StringComparison.OrdinalIgnoreCase));
        var profile = BuildUriProfile(uri, "socks", "SOCKS Server");
        ApplyUserInfo(profile, uri.UserInfo);
        return profile;
    }

    private static VmessProfile ParseHttp(string link)
    {
        var uri = CreateUri(link);
        var profile = BuildUriProfile(uri, "http", "HTTP Server");
        ApplyUserInfo(profile, uri.UserInfo);
        return profile;
    }

    private static VmessProfile BuildUriProfile(Uri uri, string protocol, string fallbackName)
    {
        if (uri.Port is <= 0 or > 65535)
        {
            throw new FormatException($"{protocol} 链接端口无效。");
        }

        return new VmessProfile
        {
            Protocol = protocol,
            Name = string.IsNullOrWhiteSpace(uri.Fragment) ? fallbackName : Uri.UnescapeDataString(uri.Fragment.TrimStart('#')),
            Remark = string.IsNullOrWhiteSpace(uri.Fragment) ? "" : Uri.UnescapeDataString(uri.Fragment.TrimStart('#')),
            Address = uri.Host,
            Port = uri.Port,
            Network = "tcp",
            Tls = ""
        };
    }

    private static Uri CreateUri(string link)
    {
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri))
        {
            throw new FormatException("节点链接格式无效。");
        }

        return uri;
    }

    private static void ApplyUserInfo(VmessProfile profile, string userInfo)
    {
        if (string.IsNullOrWhiteSpace(userInfo))
        {
            return;
        }

        var parts = userInfo.Split(':', 2);
        profile.UserId = Uri.UnescapeDataString(parts[0]);
        if (parts.Length > 1)
        {
            profile.Password = Uri.UnescapeDataString(parts[1]);
        }
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var trimmed = query.TrimStart('?');
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return result;
        }

        foreach (var item in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = item.Split('=', 2);
            var key = Uri.UnescapeDataString(parts[0]);
            var value = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "";
            result[key] = value;
        }

        return result;
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

    private static string NormalizeTls(string security)
    {
        return string.Equals(security, "tls", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(security, "reality", StringComparison.OrdinalIgnoreCase)
            ? "tls"
            : "";
    }
}
