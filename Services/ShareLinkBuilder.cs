using System.Text;
using System.Text.Json;
using NaiwaProxy.Models;

namespace NaiwaProxy.Services;

public static class ShareLinkBuilder
{
    public static string Build(VmessProfile profile)
    {
        return profile.Protocol.ToLowerInvariant() switch
        {
            "vless" => BuildVlessLink(profile),
            "trojan" => BuildTrojanLink(profile),
            "shadowsocks" or "ss" => BuildShadowsocksLink(profile),
            "socks" or "socks5" => BuildUserPassLink(profile, "socks"),
            "http" or "https" => BuildUserPassLink(profile, "http"),
            _ => BuildVmessLink(profile)
        };
    }

    private static string BuildVmessLink(VmessProfile profile)
    {
        var payload = new
        {
            v = "2",
            ps = profile.DisplayName,
            add = profile.Address,
            port = profile.Port.ToString(),
            id = profile.UserId,
            aid = profile.AlterId.ToString(),
            scy = profile.Security,
            net = profile.Network,
            type = profile.Type,
            host = profile.Host,
            path = profile.Path,
            tls = profile.Tls,
            sni = profile.Sni
        };
        return $"vmess://{Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)))}";
    }

    private static string BuildVlessLink(VmessProfile profile)
    {
        var query = BuildQuery([
            ("encryption", string.IsNullOrWhiteSpace(profile.Security) ? "none" : profile.Security),
            ("type", profile.Network),
            ("security", string.IsNullOrWhiteSpace(profile.Tls) ? "" : "tls"),
            ("sni", profile.Sni),
            ("host", profile.Host),
            ("path", profile.Path)
        ]);
        return $"vless://{Uri.EscapeDataString(profile.UserId)}@{profile.Address}:{profile.Port}{query}#{Uri.EscapeDataString(profile.DisplayName)}";
    }

    private static string BuildTrojanLink(VmessProfile profile)
    {
        var query = BuildQuery([
            ("type", profile.Network),
            ("security", string.IsNullOrWhiteSpace(profile.Tls) ? "tls" : "tls"),
            ("sni", profile.Sni),
            ("host", profile.Host),
            ("path", profile.Path)
        ]);
        return $"trojan://{Uri.EscapeDataString(profile.Password)}@{profile.Address}:{profile.Port}{query}#{Uri.EscapeDataString(profile.DisplayName)}";
    }

    private static string BuildShadowsocksLink(VmessProfile profile)
    {
        var method = string.IsNullOrWhiteSpace(profile.Security) ? "aes-128-gcm" : profile.Security;
        var userInfo = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{method}:{profile.Password}"))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return $"ss://{userInfo}@{profile.Address}:{profile.Port}#{Uri.EscapeDataString(profile.DisplayName)}";
    }

    private static string BuildUserPassLink(VmessProfile profile, string scheme)
    {
        var userInfo = string.IsNullOrWhiteSpace(profile.UserId)
            ? ""
            : $"{Uri.EscapeDataString(profile.UserId)}:{Uri.EscapeDataString(profile.Password)}@";
        return $"{scheme}://{userInfo}{profile.Address}:{profile.Port}#{Uri.EscapeDataString(profile.DisplayName)}";
    }

    private static string BuildQuery(IEnumerable<(string Key, string Value)> values)
    {
        var items = values
            .Where(item => !string.IsNullOrWhiteSpace(item.Value))
            .Select(item => $"{Uri.EscapeDataString(item.Key)}={Uri.EscapeDataString(item.Value)}")
            .ToArray();
        return items.Length == 0 ? "" : $"?{string.Join("&", items)}";
    }
}
