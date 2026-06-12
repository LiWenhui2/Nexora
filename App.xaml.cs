using System.IO;
using System.Windows;
using System.Windows.Threading;
using NaiwaProxy.Services;
using MessageBox = System.Windows.MessageBox;

namespace NaiwaProxy;

public partial class App : System.Windows.Application
{
    public App()
    {
        DiagnosticLogService.Initialize();
        DiagnosticLogService.Startup("Application constructor");
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        DiagnosticLogService.Startup("OnStartup begin");
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        try
        {
            base.OnStartup(e);
            DiagnosticLogService.Startup("Creating MainWindow");
            var mainWindow = new MainWindow();
            MainWindow = mainWindow;
            mainWindow.Show();
            DiagnosticLogService.Startup("MainWindow shown");
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Crash(ex, "OnStartup");
            MessageBox.Show(
                $"程序启动失败：{ex.Message}{Environment.NewLine}{Environment.NewLine}日志目录：{DiagnosticLogService.LogDirectory}",
                "NaiwaProxy",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        DiagnosticLogService.Crash(e.Exception, "Dispatcher");
        MessageBox.Show(
            $"程序发生未处理错误：{e.Exception.Message}{Environment.NewLine}{Environment.NewLine}日志目录：{DiagnosticLogService.LogDirectory}",
            "NaiwaProxy",
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
