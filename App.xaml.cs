using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using NaiwaProxy.Services;
using MessageBox = System.Windows.MessageBox;

namespace NaiwaProxy;

public partial class App : System.Windows.Application
{
    private const string SingleInstanceMutexName = @"Local\Nexora.Desktop.SingleInstance";
    private Mutex? _singleInstanceMutex;

    public App()
    {
        DiagnosticLogService.Initialize();
        DiagnosticLogService.Startup("Application constructor");
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        DiagnosticLogService.Startup("OnStartup begin");
        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            DiagnosticLogService.Warning("Another Nexora instance is already running. Current process will exit.");
            MessageBox.Show("Nexora 已在运行，请勿重复打开。", "Nexora", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        try
        {
            base.OnStartup(e);
            DiagnosticLogService.Startup("Creating MainWindow");
            var startSilent = e.Args.Any(arg => string.Equals(arg, StartupService.SilentArgument, StringComparison.OrdinalIgnoreCase));
            var mainWindow = new MainWindow(startSilent);
            MainWindow = mainWindow;
            mainWindow.Show();
            DiagnosticLogService.Startup(startSilent ? "MainWindow started silently to tray" : "MainWindow shown");
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Crash(ex, "OnStartup");
            MessageBox.Show(
                $"程序启动失败：{ex.Message}{Environment.NewLine}{Environment.NewLine}日志目录：{DiagnosticLogService.LogDirectory}",
                "Nexora",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _singleInstanceMutex?.ReleaseMutex();
        }
        catch
        {
        }
        finally
        {
            _singleInstanceMutex?.Dispose();
            _singleInstanceMutex = null;
        }

        base.OnExit(e);
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        DiagnosticLogService.Crash(e.Exception, "Dispatcher");
        MessageBox.Show(
            $"程序发生未处理错误：{e.Exception.Message}{Environment.NewLine}{Environment.NewLine}日志目录：{DiagnosticLogService.LogDirectory}",
            "Nexora",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            DiagnosticLogService.Crash(ex, $"AppDomain terminating={e.IsTerminating}");
        }
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        foreach (var ex in e.Exception.Flatten().InnerExceptions)
        {
            DiagnosticLogService.Crash(ex, "UnobservedTask");
        }

        e.SetObserved();
    }
}
