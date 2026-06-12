using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;

namespace NaiwaProxy.Services;

public static class IpRegionService
{
    private static readonly ConcurrentDictionary<string, string> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(8)
    };
    private static readonly SemaphoreSlim RequestGate = new(1, 1);
    private static DateTime _lastRequestUtc = DateTime.MinValue;
    private const int MinRequestIntervalMs = 1400;

    public static async Task<string?> LookupAsync(string hostOrIp, CancellationToken cancellationToken = default)
    {
        var key = hostOrIp.Trim();
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        if (Cache.TryGetValue(key, out var cached))
        {
            return cached == "-" ? null : cached;
        }

        var ip = await ResolvePublicIpAsync(key, cancellationToken);
        if (ip is null)
        {
            Cache[key] = "-";
            return null;
        }

        var ipText = ip.ToString();
        if (Cache.TryGetValue(ipText, out cached))
        {
            Cache[key] = cached;
            return cached == "-" ? null : cached;
        }

        await RequestGate.WaitAsync(cancellationToken);
        try
        {
            var waitMs = MinRequestIntervalMs - (int)(DateTime.UtcNow - _lastRequestUtc).TotalMilliseconds;
            if (waitMs > 0)
            {
                await Task.Delay(waitMs, cancellationToken);
            }

            var region = await QueryIpApiAsync(ipText, cancellationToken);
            _lastRequestUtc = DateTime.UtcNow;

            var stored = string.IsNullOrWhiteSpace(region) ? "-" : region;
            Cache[key] = stored;
            Cache[ipText] = stored;
            return stored == "-" ? null : stored;
        }
        finally
        {
            RequestGate.Release();
        }
    }

    private static async Task<string?> QueryIpApiAsync(string ip, CancellationToken cancellationToken)
    {
        using var response = await HttpClient.GetAsync(
            $"http://ip-api.com/json/{Uri.EscapeDataString(ip)}?lang=zh-CN&fields=status,country,regionName,city",
            cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        if (!root.TryGetProperty("status", out var status) ||
            !string.Equals(status.GetString(), "success", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var country = root.TryGetProperty("country", out var countryElement) ? countryElement.GetString() : null;
        var regionName = root.TryGetProperty("regionName", out var regionElement) ? regionElement.GetString() : null;
        var city = root.TryGetProperty("city", out var cityElement) ? cityElement.GetString() : null;
        return FormatLocation(country, regionName, city);
    }

    private static string? FormatLocation(string? country, string? regionName, string? city)
    {
        if (string.IsNullOrWhiteSpace(country))
        {
            return null;
        }

        var parts = new List<string> { country.Trim() };
        if (!string.IsNullOrWhiteSpace(city) &&
            !string.Equals(city, country, StringComparison.OrdinalIgnoreCase) &&
            !parts.Any(part => string.Equals(part, city, StringComparison.OrdinalIgnoreCase)))
        {
            parts.Add(city.Trim());
        }
        else if (!string.IsNullOrWhiteSpace(regionName) &&
                 !string.Equals(regionName, country, StringComparison.OrdinalIgnoreCase) &&
                 !parts.Any(part => string.Equals(part, regionName, StringComparison.OrdinalIgnoreCase)))
        {
            parts.Add(regionName.Trim());
        }

        return string.Join("·", parts);
    }

    private static async Task<IPAddress?> ResolvePublicIpAsync(string hostOrIp, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(hostOrIp, out var parsed))
        {
            return IsPublicIp(parsed) ? parsed : null;
        }

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(hostOrIp, cancellationToken);
            return addresses.FirstOrDefault(IsPublicIp);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsPublicIp(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip))
        {
            return false;
        }

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = ip.GetAddressBytes();
            return bytes[0] switch
            {
                10 => false,
                127 => false,
                0 => false,
                169 when bytes[1] == 254 => false,
                172 when bytes[1] is >= 16 and <= 31 => false,
                192 when bytes[1] == 168 => false,
                _ => true
            };
        }

        if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal)
        {
            return false;
        }

        return true;
    }
}
