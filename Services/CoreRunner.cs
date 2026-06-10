using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;

namespace NaiwaProxy.Services;

public static class CoreRunner
{
    public static string ResolveCorePath(string executable)
    {
        if (Path.IsPathFullyQualified(executable))
        {
            return executable;
        }

        return Path.Combine(AppContext.BaseDirectory, "cores", executable);
    }

    public static Process Start(string coreExecutable, string configPath)
    {
        var corePath = ResolveCorePath(coreExecutable);
        if (!File.Exists(corePath))
        {
            throw new FileNotFoundException($"Core executable was not found: {corePath}");
        }

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = corePath,
                Arguments = $"-config \"{configPath}\"",
                WorkingDirectory = Path.GetDirectoryName(corePath) ?? AppContext.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };
        process.Start();
        return process;
    }

    public static void Stop(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
            }
        }
        catch
        {
            // Best effort cleanup for ephemeral test processes.
        }
        finally
        {
            process.Dispose();
        }
    }

    public static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public static async Task WaitForPortAsync(int port, int timeoutMs, CancellationToken cancellationToken = default)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeoutMs);

        while (!timeoutSource.Token.IsCancellationRequested)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port, timeoutSource.Token);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                await Task.Delay(100, timeoutSource.Token);
            }
        }
    }
}
