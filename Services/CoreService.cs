using System.Diagnostics;
using System.IO;
using NaiwaProxy.Models;

namespace NaiwaProxy.Services;

public sealed class CoreService
{
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
        Stop();
        File.WriteAllText(ConfigPath, CoreConfigBuilder.Build(settings, profile));
        _process = CoreRunner.Start(settings.CoreExecutable, ConfigPath);
    }

    public void Stop()
    {
        CoreRunner.Stop(_process);
        _process = null;
    }
}
