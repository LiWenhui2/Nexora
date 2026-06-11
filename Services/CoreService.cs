using System.Diagnostics;
using System.IO;
using NaiwaProxy.Models;

namespace NaiwaProxy.Services;

public sealed class CoreService
{
    private const int StartupTimeoutMs = 10000;
    private Process? _process;

    public bool IsRunning => _process is { HasExited: false };

    public string ConfigPath
    {
        get
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "NaiwaProxy");
            Directory.CreateDirectory(directory);
            return Path.Combine(directory, "config.json");
        }
    }

    public void Start(AppSettings settings, VmessProfile profile)
    {
        StartAsync(settings, profile).GetAwaiter().GetResult();
    }

    public async Task StartAsync(AppSettings settings, VmessProfile profile)
    {
        Stop();
        File.WriteAllText(ConfigPath, CoreConfigBuilder.Build(settings, profile));
        var process = CoreRunner.Start(settings.CoreExecutable, ConfigPath);
        _process = process;

        try
        {
            await CoreRunner.WaitForPortAsync(settings.HttpPort, StartupTimeoutMs);
            await CoreRunner.WaitForPortAsync(settings.SocksPort, StartupTimeoutMs);
            await CoreRunner.WaitForPortAsync(settings.ApiPort, StartupTimeoutMs);
        }
        catch
        {
            Stop();
            throw new InvalidOperationException("Core 启动失败，请检查节点配置或端口是否被占用。");
        }

        if (process.HasExited)
        {
            Stop();
            throw new InvalidOperationException("Core 已退出，请检查节点配置。");
        }
    }

    public void Stop()
    {
        CoreRunner.Stop(_process);
        _process = null;
    }
}
