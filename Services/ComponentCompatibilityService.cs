using System.Diagnostics;
using System.IO;

namespace NaiwaProxy.Services;

public sealed record ComponentCompatibilityResult(bool Success, IReadOnlyList<string> Details)
{
    public string Summary => Success ? "核心组件兼容性正常" : "核心组件需要处理";
}

public static class ComponentCompatibilityService
{
    public static async Task<ComponentCompatibilityResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        var details = new List<string>();
        var success = true;
        var xray = CoreRunner.ResolveCorePath("xray.exe");
        success &= await CheckExecutableAsync(xray, "version", "Xray", details, cancellationToken);
        success &= await CheckExecutableAsync(TunService.SingBoxPath, "version", "sing-box", details, cancellationToken);

        if (!TunService.HasWintun)
        {
            details.Add("✕ Wintun：文件缺失");
            success = false;
        }
        else
        {
            var architecture = ReadPeArchitecture(TunService.WintunPath);
            var expected = Environment.Is64BitProcess ? "x64" : "x86";
            var matched = architecture == expected;
            details.Add($"{(matched ? "✓" : "✕")} Wintun：{architecture}（应用 {expected}）");
            success &= matched;
        }

        return new ComponentCompatibilityResult(success, details);
    }

    private static async Task<bool> CheckExecutableAsync(string path, string arguments, string name, List<string> details, CancellationToken token)
    {
        if (!File.Exists(path)) { details.Add($"✕ {name}：文件缺失"); return false; }
        try
        {
            using var process = Process.Start(new ProcessStartInfo(path, arguments)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            });
            if (process is null) throw new InvalidOperationException("无法启动");
            var outputTask = process.StandardOutput.ReadLineAsync(token).AsTask();
            await process.WaitForExitAsync(token);
            var version = (await outputTask)?.Trim() ?? "版本未知";
            details.Add($"✓ {name}：{version}");
            return process.ExitCode == 0;
        }
        catch (Exception ex) { details.Add($"✕ {name}：{ex.Message}"); return false; }
    }

    private static string ReadPeArchitecture(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);
            stream.Position = 0x3C;
            stream.Position = reader.ReadInt32() + 4;
            return reader.ReadUInt16() switch { 0x8664 => "x64", 0x14C => "x86", 0xAA64 => "arm64", _ => "未知" };
        }
        catch { return "未知"; }
    }
}
