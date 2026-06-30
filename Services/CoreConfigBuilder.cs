using System.IO;
using System.Text.Json;
using NaiwaProxy.Models;

namespace NaiwaProxy.Services;

public static class CoreConfigBuilder
{
    public static string Build(AppSettings settings, VmessProfile profile, string accessLogPath, string errorLogPath)
    {
        var streamSettings = BuildStreamSettings(profile);
        var proxyOutbound = BuildProxyOutbound(profile, streamSettings);
        var config = new
        {
            log = new
            {
                access = accessLogPath,
                error = errorLogPath,
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
                    listen = GetInboundListenAddress(settings),
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
                    listen = GetInboundListenAddress(settings),
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

        AddCustomOverlayRules(rules, settings.CustomRouting);
        AddBuiltInDirectRules(rules);

        switch (settings.RoutingMode)
        {
            case "Custom":
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

    private static void AddCustomOverlayRules(List<object> rules, CustomRoutingSettings routing)
    {
        AddCustomRule(rules, routing.BlockDomains, routing.BlockIps, routing.BlockProcesses, "block");
        AddCustomRule(rules, routing.DirectDomains, routing.DirectIps, routing.DirectProcesses, "direct");
        AddBypassChinaRules(rules, routing.BypassChinaDomains, routing.BypassChinaIps, routing.BypassChinaProcesses);
        AddCustomRule(rules, routing.ProxyDomains, routing.ProxyIps, routing.ProxyProcesses, "proxy");
    }

    private static void AddBuiltInDirectRules(List<object> rules)
    {
        AddCustomRule(
            rules,
            BuiltInMicrosoftStoreDomainRules.ToList(),
            [],
            [],
            "direct");
        AddCustomRule(
            rules,
            [],
            [],
            BuiltInMicrosoftStoreProcessRules.ToList(),
            "direct");
    }

    private static void AddBypassChinaRules(
        List<object> rules,
        List<string> domains,
        List<string> ips,
        List<string> processes)
    {
        AddCustomRule(rules, domains, ips, processes, "direct");

        var normalizedProcesses = processes
            .Select(NormalizeProcessRule)
            .Where(rule => !string.IsNullOrWhiteSpace(rule))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (normalizedProcesses.Length == 0)
        {
            return;
        }

        rules.Add(new Dictionary<string, object>
        {
            ["type"] = "field",
            ["process"] = normalizedProcesses,
            ["ip"] = new[] { "geoip:private", "geoip:cn" },
            ["outboundTag"] = "direct"
        });
        rules.Add(new Dictionary<string, object>
        {
            ["type"] = "field",
            ["process"] = normalizedProcesses,
            ["domain"] = new[] { "geosite:cn" },
            ["outboundTag"] = "direct"
        });
    }

    private static void AddCustomRule(
        List<object> rules,
        List<string> domains,
        List<string> ips,
        List<string> processes,
        string outboundTag)
    {
        var normalizedDomains = domains
            .SelectMany(ExpandDomainRule)
            .Where(rule => !string.IsNullOrWhiteSpace(rule))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var normalizedIps = ips
            .Select(NormalizeIpRule)
            .Where(rule => !string.IsNullOrWhiteSpace(rule))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var normalizedProcesses = processes
            .Select(NormalizeProcessRule)
            .Where(rule => !string.IsNullOrWhiteSpace(rule))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (normalizedDomains.Length == 0 && normalizedIps.Length == 0 && normalizedProcesses.Length == 0)
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

        if (normalizedProcesses.Length > 0)
        {
            rule["process"] = normalizedProcesses;
        }

        rules.Add(rule);
    }

    private static IEnumerable<string> ExpandDomainRule(string value)
    {
        var normalized = NormalizeDomainRule(value);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            yield break;
        }

        if (IsWindowsServiceGeositeAlias(normalized))
        {
            foreach (var replacement in WindowsServiceDomainRules)
            {
                yield return replacement;
            }

            yield break;
        }

        yield return normalized;
    }

    private static bool IsWindowsServiceGeositeAlias(string rule) =>
        string.Equals(rule, "geosite:windows", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(rule, "geosite:win-spy", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(rule, "geosite:microsoft", StringComparison.OrdinalIgnoreCase);

    private static readonly string[] WindowsServiceDomainRules =
    [
        "domain:microsoft.com",
        "domain:windows.com",
        "domain:live.com",
        "domain:microsoftonline.com",
        "domain:xboxlive.com",
        "domain:msftconnecttest.com",
        "domain:msftncsi.com",
        "domain:mp.microsoft.com"
    ];

    private static readonly string[] BuiltInMicrosoftStoreDomainRules =
    [
        "domain:microsoft.com",
        "domain:windows.com",
        "domain:live.com",
        "domain:microsoftonline.com",
        "domain:xboxlive.com",
        "domain:msftconnecttest.com",
        "domain:msftncsi.com",
        "domain:mp.microsoft.com",
        "domain:delivery.mp.microsoft.com",
        "domain:storeedgefd.dsx.mp.microsoft.com",
        "domain:displaycatalog.mp.microsoft.com",
        "domain:purchase.mp.microsoft.com",
        "domain:licensing.mp.microsoft.com"
    ];

    private static readonly string[] BuiltInMicrosoftStoreProcessRules =
    [
        "WinStore.App.exe",
        "Microsoft.WindowsStore.exe",
        "RuntimeBroker.exe"
    ];

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

    private static string NormalizeIpRule(string value)
    {
        var trimmed = value.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? "" : trimmed;
    }

    private static string NormalizeProcessRule(string value)
    {
        var trimmed = value.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return "";
        }

        var fileName = Path.GetFileName(trimmed);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return "";
        }

        return fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? fileName : $"{fileName}.exe";
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

    private static string GetInboundListenAddress(AppSettings settings) =>
        settings.AllowLanAccess ? "0.0.0.0" : "127.0.0.1";
}
