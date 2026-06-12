using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace NaiwaProxy.Services;

public static class DiagnosticLogService
{
    private static readonly object Sync = new();
    private static string _logDirectory = string.Empty;
    private static bool _initialized;

    public static string LogDirectory => _logDirectory;

    public static string AppLogPath => Path.Combine(_logDirectory, "app.log");
    public static string StartupLogPath => Path.Combine(_logDirectory, "startup.log");
    public static string CrashLogPath => Path.Combine(_logDirectory, "crash.log");

    public static void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        _logDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "NaiwaProxy",
            "logs");
        Directory.CreateDirectory(_logDirectory);

        WriteStartupBanner();
        _initialized = true;
    }

    public static void Startup(string message) => Write("startup.log", "STARTUP", message);

    public static void Info(string message) => Write("app.log", "INFO", message);

    public static void Warning(string message) => Write("app.log", "WARN", message);

    public static void Error(string message, Exception? exception = null)
    {
        var body = exception is null ? message : $"{message}{Environment.NewLine}{exception}";
        Write("app.log", "ERROR", body);
    }

    public static void Crash(Exception exception, string source)
    {
        var body = new StringBuilder()
            .AppendLine($"Source: {source}")
            .AppendLine($"Message: {exception.Message}")
            .AppendLine(exception.ToString())
            .ToString();
        Write("crash.log", "CRASH", body);
        Write("app.log", "CRASH", body);
    }

    private static void WriteStartupBanner()
    {
        var banner = new StringBuilder()
            .AppendLine("======== NaiwaProxy startup ========")
            .AppendLine($"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}")
            .AppendLine($"Version: {GetAppVersion()}")
            .AppendLine($"OS: {RuntimeInformation.OSDescription}")
            .AppendLine($"64-bit process: {Environment.Is64BitProcess}")
            .AppendLine($"Base directory: {AppContext.BaseDirectory}")
            .AppendLine($"Working directory: {Environment.CurrentDirectory}")
            .AppendLine($"User: {Environment.UserName}")
            .AppendLine($"Machine: {Environment.MachineName}")
            .AppendLine($"CLR: {RuntimeInformation.FrameworkDescription}")
            .AppendLine("====================================")
            .ToString();

        lock (Sync)
        {
            File.AppendAllText(StartupLogPath, banner, Encoding.UTF8);
        }
    }

    private static void Write(string fileName, string level, string message)
    {
        if (!_initialized)
        {
            Initialize();
        }

        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}{Environment.NewLine}";
        lock (Sync)
        {
            File.AppendAllText(Path.Combine(_logDirectory, fileName), line, Encoding.UTF8);
        }
    }

    private static string GetAppVersion()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        return version?.ToString() ?? "unknown";
    }

    public static void OpenLogDirectory()
    {
        if (!_initialized)
        {
            Initialize();
        }

        Directory.CreateDirectory(_logDirectory);
        Process.Start(new ProcessStartInfo
        {
            FileName = _logDirectory,
            UseShellExecute = true
        });
    }
}
