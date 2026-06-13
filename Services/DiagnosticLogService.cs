using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace NaiwaProxy.Services;

public enum LogCategory
{
    System,
    Traffic
}

public sealed class LogFilter
{
    public bool ShowInfo { get; set; } = true;
    public bool ShowWarn { get; set; } = true;
    public bool ShowError { get; set; } = true;
    public bool ShowCrash { get; set; } = true;
    public bool ShowSystem { get; set; } = true;
    public bool ShowTraffic { get; set; } = true;
}

public sealed class LogEntry
{
    public DateTime Timestamp { get; init; }
    public LogCategory Category { get; init; }
    public string Level { get; init; } = "";
    public string Message { get; init; } = "";

    public string CategoryLabel => Category == LogCategory.Traffic ? "Traffic" : "System";

    public string DisplayLine => Category == LogCategory.Traffic
        ? Message
        : $"[{Timestamp:HH:mm:ss}] [{Level}] [System] {Message}";

    public bool Matches(LogFilter filter)
    {
        if (Category == LogCategory.System && !filter.ShowSystem)
        {
            return false;
        }

        if (Category == LogCategory.Traffic && !filter.ShowTraffic)
        {
            return false;
        }

        return Level switch
        {
            "INFO" => filter.ShowInfo,
            "WARN" => filter.ShowWarn,
            "ERROR" => filter.ShowError,
            "CRASH" => filter.ShowCrash,
            _ => true
        };
    }
}

public static class DiagnosticLogService
{
    private static readonly object Sync = new();
    private static readonly List<LogEntry> Entries = [];
    private const int MaxEntries = 3000;
    private static string _logDirectory = string.Empty;
    private static bool _initialized;

    public static event Action<LogEntry>? EntryAdded;

    public static string LogDirectory => _logDirectory;

    public static string AppLogPath => Path.Combine(_logDirectory, "app.log");
    public static string AccessLogPath => Path.Combine(_logDirectory, "access.log");
    public static string TrafficMonitorLogPath => Path.Combine(_logDirectory, "traffic-monitor.log");
    public static string CoreErrorLogPath => Path.Combine(_logDirectory, "core-error.log");
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

    public static void Startup(string message) => WriteFile("startup.log", "STARTUP", message);

    public static void Info(string message) => Add(LogCategory.System, "INFO", message, "app.log");

    public static void Warning(string message) => Add(LogCategory.System, "WARN", message, "app.log");

    public static void Error(string message, Exception? exception = null)
    {
        var body = exception is null ? message : $"{message}{Environment.NewLine}{exception}";
        Add(LogCategory.System, "ERROR", body, "app.log");
    }

    public static void Crash(Exception exception, string source)
    {
        var body = new StringBuilder()
            .AppendLine($"Source: {source}")
            .AppendLine($"Message: {exception.Message}")
            .AppendLine(exception.ToString())
            .ToString();
        WriteFile("crash.log", "CRASH", body);
        Add(LogCategory.System, "CRASH", body, "app.log", writeFile: false);
    }

    public static void Traffic(string line)
    {
        Add(LogCategory.Traffic, "INFO", line, "traffic-monitor.log");
    }

    public static IReadOnlyList<LogEntry> GetRecentEntries(LogFilter? filter = null)
    {
        lock (Sync)
        {
            var query = Entries.AsEnumerable();
            if (filter is not null)
            {
                query = query.Where(entry => entry.Matches(filter));
            }

            return query.ToList();
        }
    }

    public static string GetDisplayText(LogFilter? filter = null)
    {
        var entries = GetRecentEntries(filter);
        if (entries.Count == 0)
        {
            return "No logs match the current filters.";
        }

        return string.Join(Environment.NewLine, entries.Select(entry => entry.DisplayLine));
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

    private static void Add(
        LogCategory category,
        string level,
        string message,
        string fileName,
        bool writeFile = true)
    {
        if (!_initialized)
        {
            Initialize();
        }

        if (writeFile)
        {
            WriteFile(fileName, level, message, raw: category == LogCategory.Traffic);
        }

        if (!ShouldDisplay(category, level, message))
        {
            return;
        }

        var entry = new LogEntry
        {
            Timestamp = DateTime.Now,
            Category = category,
            Level = level,
            Message = SimplifyMessage(message)
        };

        lock (Sync)
        {
            Entries.Add(entry);
            if (Entries.Count > MaxEntries)
            {
                Entries.RemoveRange(0, Entries.Count - MaxEntries);
            }
        }

        EntryAdded?.Invoke(entry);
    }

    private static void WriteFile(string fileName, string level, string message, bool raw = false)
    {
        if (!_initialized)
        {
            Initialize();
        }

        var line = raw
            ? $"{message}{Environment.NewLine}"
            : $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}{Environment.NewLine}";
        lock (Sync)
        {
            File.AppendAllText(Path.Combine(_logDirectory, fileName), line, Encoding.UTF8);
        }
    }

    private static bool ShouldDisplay(LogCategory category, string level, string message)
    {
        if (category == LogCategory.Traffic)
        {
            return true;
        }

        if (level is not ("INFO" or "WARN" or "ERROR" or "CRASH"))
        {
            return false;
        }

        return !message.Contains("Startup latency test", StringComparison.OrdinalIgnoreCase);
    }

    private static string SimplifyMessage(string message)
    {
        var firstLine = message.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";
        if (firstLine.Length > 220)
        {
            firstLine = string.Concat(firstLine.AsSpan(0, 220), "...");
        }

        return firstLine;
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
