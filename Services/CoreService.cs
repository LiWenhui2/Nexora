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

        var corePath = ResolveCorePath(settings.CoreExecutable);
        if (!File.Exists(corePath))
        {
            throw new FileNotFoundException($"Core executable was not found: {corePath}");
        }

        File.WriteAllText(ConfigPath, CoreConfigBuilder.Build(settings, profile));

        _process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = corePath,
                Arguments = $"-config \"{ConfigPath}\"",
                WorkingDirectory = Path.GetDirectoryName(corePath) ?? AppContext.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };
        _process.Start();
    }

    public void Stop()
    {
        if (_process is null)
        {
            return;
        }

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(3000);
            }
        }
        finally
        {
            _process.Dispose();
            _process = null;
        }
    }

    private static string ResolveCorePath(string executable)
    {
        if (Path.IsPathFullyQualified(executable))
        {
            return executable;
        }

        return Path.Combine(AppContext.BaseDirectory, "cores", executable);
    }
}
