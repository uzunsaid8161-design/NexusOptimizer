using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using NexusOptimizer.Services;
using NexusOptimizer.ViewModels;
using Serilog;

namespace NexusOptimizer;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    private static Mutex? _instanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instanceMutex = new Mutex(true, @"Local\NexusOptimizer.SingleInstance", out bool isFirstInstance);
        if (!isFirstInstance)
        {
            ActivateRunningInstance();
            Shutdown();
            return;
        }

        Directory.CreateDirectory(AppPaths.Logs);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(Path.Combine(AppPaths.Logs, "nexus-.log"), rollingInterval: RollingInterval.Day)
            .CreateLogger();

        var services = new ServiceCollection();
        services.AddSingleton<FpsSampler>();
        services.AddSingleton<SystemMonitor>();
        services.AddSingleton<Database>();
        services.AddSingleton<OptimizerService>();
        services.AddSingleton<DashboardViewModel>();
        services.AddSingleton<OptimizerViewModel>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();
        Services = services.BuildServiceProvider();

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Error(args.ExceptionObject as Exception, "Unhandled");

        try
        {
            Services.GetRequiredService<Database>().Initialize();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Datenbank-Initialisierung fehlgeschlagen");
        }

        Services.GetRequiredService<MainWindow>().Show();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "UI-Exception");
        e.Handled = true;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private static void ActivateRunningInstance()
    {
        try
        {
            foreach (var process in System.Diagnostics.Process.GetProcessesByName("NexusOptimizer"))
            {
                if (process.MainWindowHandle == IntPtr.Zero) continue;
                ShowWindow(process.MainWindowHandle, 9); // SW_RESTORE
                SetForegroundWindow(process.MainWindowHandle);
                break;
            }
        }
        catch { }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
