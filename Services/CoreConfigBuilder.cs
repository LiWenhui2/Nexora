using System.Text.Json;
using NaiwaProxy.Models;

namespace NaiwaProxy.Services;

public static class CoreConfigBuilder
{
    public static string Build(AppSettings settings, VmessProfile profile)
    {
        var streamSettings = BuildStreamSettings(profile);
        var config = new
        {
            log = new
            {
                loglevel = "warning"
            },
            inbounds = new object[]
            {
                new
                {
                    tag = "socks",
                    port = settings.SocksPort,
                    listen = "127.0.0.1",
                    protocol = "socks",
                    settings = new
                    {
                        udp = true,
                        auth = "noauth"
                    }
                },
                new
                {
                    tag = "http",
                    port = settings.HttpPort,
                    listen = "127.0.0.1",
                    protocol = "http"
                }
            },
            outbounds = new object[]
            {
                new
                {
                    tag = "proxy",
                    protocol = "vmess",
                    settings = new
                    {
                        vnext = new object[]
                        {
                            new
                            {
                                address = profile.Address,
                                port = profile.Port,
                                users = new object[]
                                {
                                    new
                                    {
                                        id = profile.UserId,
                                        alterId = profile.AlterId,
                                        security = profile.Security
                                    }
                                }
                            }
                        }
                    },
                    streamSettings
                },
                new
                {
                    tag = "direct",
                    protocol = "freedom"
                },
                new
                {
                    tag = "block",
                    protocol = "blackhole"
                }
            },
            routing = new
            {
                domainStrategy = "AsIs",
                rules = Array.Empty<object>()
            }
        };

        return JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
    }

    private static object BuildStreamSettings(VmessProfile profile)
    {
        object? transportSettings = profile.Network.ToLowerInvariant() switch
        {
            "ws" => new
            {
                path = string.IsNullOrWhiteSpace(profile.Path) ? "/" : profile.Path,
                headers = string.IsNullOrWhiteSpace(profile.Host)
                    ? new Dictionary<string, string>()
                    : new Dictionary<string, string> { ["Host"] = profile.Host }
            },
            "tcp" => string.Equals(profile.Type, "http", StringComparison.OrdinalIgnoreCase)
                ? new
                {
                    header = new
                    {
                        type = "http",
                        request = new
                        {
                            path = string.IsNullOrWhiteSpace(profile.Path) ? new[] { "/" } : new[] { profile.Path },
                            headers = string.IsNullOrWhiteSpace(profile.Host)
                                ? new Dictionary<string, string[]>()
                                : new Dictionary<string, string[]> { ["Host"] = new[] { profile.Host } }
                        }
                    }
                }
                : null,
            _ => null
        };

        var network = string.IsNullOrWhiteSpace(profile.Network) ? "tcp" : profile.Network.ToLowerInvariant();
        var tls = profile.Tls.Equals("tls", StringComparison.OrdinalIgnoreCase) ? "tls" : "none";
        var result = new Dictionary<string, object?>
        {
            ["network"] = network,
            ["security"] = tls
        };

        if (tls == "tls")
        {
            result["tlsSettings"] = new
            {
                serverName = string.IsNullOrWhiteSpace(profile.Sni) ? profile.Host : profile.Sni,
                allowInsecure = false
            };
        }

        if (transportSettings is not null)
        {
            result[$"{network}Settings"] = transportSettings;
        }

        return result;
    }
}
