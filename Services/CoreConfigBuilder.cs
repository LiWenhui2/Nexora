using System.Text.Json;
using NaiwaProxy.Models;

namespace NaiwaProxy.Services;

public static class CoreConfigBuilder
{
    public static string Build(AppSettings settings, VmessProfile profile)
    {
        var streamSettings = BuildStreamSettings(profile);
        var proxyOutbound = BuildProxyOutbound(profile, streamSettings);
        var config = new
        {
            log = new
            {
                access = DiagnosticLogService.AccessLogPath,
                error = DiagnosticLogService.CoreErrorLogPath,
                loglevel = "warning"
            },
            api = new
            {
                tag = "api",
                services = new[] { "StatsService" }
            },
            stats = new { },
            policy = new
            {
                system = new
                {
                    statsInboundUplink = true,
                    statsInboundDownlink = true,
                    statsOutboundUplink = true,
                    statsOutboundDownlink = true
                }
            },
            inbounds = new object[]
            {
                new
                {
                    tag = "api",
                    port = settings.ApiPort,
                    listen = "127.0.0.1",
                    protocol = "dokodemo-door",
                    settings = new
                    {
                        address = "127.0.0.1"
                    }
                },
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
                proxyOutbound,
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
            routing = BuildRouting(settings)
        };

        return JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
    }

    private static object BuildProxyOutbound(VmessProfile profile, object streamSettings)
    {
        return profile.Protocol.ToLowerInvariant() switch
        {
            "vless" => new
            {
                tag = "proxy",
                protocol = "vless",
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
                                    encryption = string.IsNullOrWhiteSpace(profile.Security) ? "none" : profile.Security
                                }
                            }
                        }
                    }
                },
                streamSettings
            },
            "trojan" => new
            {
                tag = "proxy",
                protocol = "trojan",
                settings = new
                {
                    servers = new object[]
                    {
                        new
                        {
                            address = profile.Address,
                            port = profile.Port,
                            password = profile.Password
                        }
                    }
                },
                streamSettings
            },
            "shadowsocks" or "ss" => new
            {
                tag = "proxy",
                protocol = "shadowsocks",
                settings = new
                {
                    servers = new object[]
                    {
                        new
                        {
                            address = profile.Address,
                            port = profile.Port,
                            method = string.IsNullOrWhiteSpace(profile.Security) ? "aes-128-gcm" : profile.Security,
                            password = profile.Password
                        }
                    }
                }
            },
            "socks" or "socks5" => new
            {
                tag = "proxy",
                protocol = "socks",
                settings = new
                {
                    servers = BuildUserPassServers(profile)
                }
            },
            "http" or "https" => new
            {
                tag = "proxy",
                protocol = "http",
                settings = new
                {
                    servers = BuildUserPassServers(profile)
                }
            },
            _ => new
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
            }
        };
    }

    private static object[] BuildUserPassServers(VmessProfile profile)
    {
        var server = new Dictionary<string, object>
        {
            ["address"] = profile.Address,
            ["port"] = profile.Port
        };

        if (!string.IsNullOrWhiteSpace(profile.UserId))
        {
            server["users"] = new object[]
            {
                new
                {
                    user = profile.UserId,
                    pass = profile.Password
                }
            };
        }

        return [server];
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

    private static object BuildRouting(AppSettings settings)
    {
        var rules = new List<object>
        {
            new
            {
                type = "field",
                inboundTag = new[] { "api" },
                outboundTag = "api"
            }
        };

        switch (settings.RoutingMode)
        {
            case "Custom":
                AddCustomRule(rules, settings.CustomRouting.BlockDomains, settings.CustomRouting.BlockIps, "block");
                AddCustomRule(rules, settings.CustomRouting.DirectDomains, settings.CustomRouting.DirectIps, "direct");
                AddCustomRule(rules, settings.CustomRouting.ProxyDomains, settings.CustomRouting.ProxyIps, "proxy");
                break;
            case "Direct":
                rules.Add(new
                {
                    type = "field",
                    network = "tcp,udp",
                    outboundTag = "direct"
                });
                break;
            case "BypassLan":
                rules.Add(BuildPrivateDirectRule());
                break;
            case "BypassChina":
                rules.Add(BuildPrivateDirectRule());
                rules.Add(new
                {
                    type = "field",
                    ip = new[] { "geoip:cn" },
                    outboundTag = "direct"
                });
                rules.Add(new
                {
                    type = "field",
                    domain = new[] { "geosite:cn" },
                    outboundTag = "direct"
                });
                break;
            case "Global":
            default:
                break;
        }

        return new
        {
            domainStrategy = "IPIfNonMatch",
            rules
        };
    }

    private static void AddCustomRule(List<object> rules, List<string> domains, List<string> ips, string outboundTag)
    {
        var normalizedDomains = domains
            .Select(NormalizeDomainRule)
            .Where(rule => !string.IsNullOrWhiteSpace(rule))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var normalizedIps = ips
            .Select(rule => rule.Trim())
            .Where(rule => !string.IsNullOrWhiteSpace(rule))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (normalizedDomains.Length == 0 && normalizedIps.Length == 0)
        {
            return;
        }

        var rule = new Dictionary<string, object>
        {
            ["type"] = "field",
            ["outboundTag"] = outboundTag
        };

        if (normalizedDomains.Length > 0)
        {
            rule["domain"] = normalizedDomains;
        }

        if (normalizedIps.Length > 0)
        {
            rule["ip"] = normalizedIps;
        }

        rules.Add(rule);
    }

    private static string NormalizeDomainRule(string value)
    {
        var trimmed = value.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return "";
        }

        if (trimmed.Contains("://", StringComparison.Ordinal))
        {
            trimmed = new Uri(trimmed).Host;
        }

        if (trimmed.StartsWith("domain:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("full:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("regexp:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("geosite:", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        return $"domain:{trimmed}";
    }

    private static object BuildPrivateDirectRule()
    {
        return new
        {
            type = "field",
            ip = new[]
            {
                "geoip:private",
                "127.0.0.0/8",
                "224.0.0.0/4"
            },
            outboundTag = "direct"
        };
    }
}
