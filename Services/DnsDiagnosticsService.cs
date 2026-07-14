using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using NaiwaProxy.Models;

namespace NaiwaProxy.Services;

public sealed record DnsDiagnosticsResult(bool Success, IReadOnlyList<string> Details);

public static class DnsDiagnosticsService
{
    public static async Task<DnsDiagnosticsResult> RunAsync(AppSettings settings, CancellationToken token = default)
    {
        var details = new List<string>();
        var success = true;
        try
        {
            var addresses = await Dns.GetHostAddressesAsync("www.cloudflare.com", token);
            var hasV4 = addresses.Any(x => x.AddressFamily == AddressFamily.InterNetwork);
            var hasV6 = addresses.Any(x => x.AddressFamily == AddressFamily.InterNetworkV6);
            details.Add($"✓ 系统 DNS：IPv4 {(hasV4 ? "可用" : "不可用")}，IPv6 {(hasV6 ? "可用" : "不可用")}");
            if (settings.IpPreferenceMode == "IPv4Only" && !hasV4) success = false;
            if (settings.IpPreferenceMode == "PreferIPv6" && !hasV6 && !settings.Ipv6AutoFallbackEnabled) success = false;
        }
        catch (Exception ex)
        {
            details.Add($"✕ 系统 DNS：{ex.Message}");
            success = false;
        }

        var configured = $"国内 {settings.DomesticDnsServer}；代理 {settings.ProxyDnsServer}" +
                         (settings.DnsOverHttpsEnabled ? "（DoH）" : "（UDP）");
        details.Add($"✓ DNS 配置：{configured}");
        var activeDns = NetworkInterface.GetAllNetworkInterfaces()
            .Where(x => x.OperationalStatus == OperationalStatus.Up)
            .SelectMany(x => x.GetIPProperties().DnsAddresses)
            .Distinct()
            .Take(6)
            .ToList();
        details.Add($"{(settings.IsTunEnabled && TunService.IsRunning ? "✓" : "!")} TUN DNS：" +
                    (settings.IsTunEnabled
                        ? (TunService.IsRunning ? "DNS 劫持运行中" : "TUN 已配置但未运行")
                        : "未启用 TUN"));
        details.Add("系统网卡 DNS：" + (activeDns.Count == 0 ? "未发现" : string.Join(", ", activeDns)));
        details.Add(settings.DnsOverHttpsEnabled
            ? "DNS 泄漏防护：代理 DNS 使用 DoH 并经代理出口发送"
            : "DNS 泄漏提示：代理 DNS 当前使用 UDP，建议启用 DoH");
        if (settings.IsTunEnabled && !TunService.IsRunning) success = false;
        return new DnsDiagnosticsResult(success, details);
    }
}
