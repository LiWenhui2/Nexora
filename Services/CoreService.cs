using System.Diagnostics;
using System.IO;
using NaiwaProxy.Models;

namespace NaiwaProxy.Services;

public sealed class CoreService
{
    private const int StartupTimeoutMs = 10000;
    private readonly CoreAccessLogService _accessLogService = new();
    private Process? _process;
    private int _httpPort;

    public event EventHandler? CoreExited;

    public bool IsRunning => _process is { HasExited: false };

    public string ConfigPath
    {
        get
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Nexora");
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
        Stop(settings);
        _httpPort = settings.HttpPort;
        File.WriteAllText(ConfigPath, CoreConfigBuilder.Build(settings, profile));
        var process = CoreRunner.Start(settings.CoreExecutable, ConfigPath);
        process.EnableRaisingEvents = true;
        process.Exited += OnProcessExited;
        _process = process;

        try
        {
            await CoreRunner.WaitForPortAsync(settings.HttpPort, StartupTimeoutMs);
            await CoreRunner.WaitForPortAsync(settings.SocksPort, StartupTimeoutMs);
            await CoreRunner.WaitForPortAsync(settings.ApiPort, StartupTimeoutMs);
        }
        catch
        {
            Stop(settings);
            throw new InvalidOperationException("Core 启动失败，请检查节点配置或端口是否被占用。");
        }

        if (process.HasExited)
        {
            Stop(settings);
            throw new InvalidOperationException("Core 已退出，请检查节点配置。");
        }

        _accessLogService.Start(DiagnosticLogService.AccessLogPath);
    }

    public void Stop(AppSettings? settings = null)
    {
        _accessLogService.Stop();
        if (_process is not null)
        {
            _process.Exited -= OnProcessExited;
            CoreRunner.Stop(_process);
            _process = null;
        }

        _httpPort = 0;

        if (settings is not null)
        {
            CoreRunner.ReleasePorts(settings.HttpPort, settings.SocksPort, settings.ApiPort);
        }
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        if (sender is not Process exitedProcess || !ReferenceEquals(exitedProcess, _process))
        {
            return;
        }

        exitedProcess.Exited -= OnProcessExited;
        _process = null;
        _httpPort = 0;
        CoreExited?.Invoke(this, EventArgs.Empty);
    }
}
