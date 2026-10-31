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
        // WPF writes temporary cursor files when GridView headers are hovered.
        // Use an app-owned writable folder if the Windows TEMP folder is restricted.
        try
        {
            string appTemp = Path.Combine(AppPaths.Root, "temp");
            Directory.CreateDirectory(appTemp);
            Environment.SetEnvironmentVariable("TEMP", appTemp);
            Environment.SetEnvironmentVariable("TMP", appTemp);
        }
        catch { }

        base.OnStartup(e);

        _instanceMutex = new Mutex(true, @"Local\NexusOptimizer.SingleInstance", out bool isFirstInstance);
        bool allowMultiple = e.Args.Contains("--new-instance", StringComparer.OrdinalIgnoreCase);
        if (!isFirstInstance && !allowMultiple)
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
        Log.Information("NexusOptimizer startup entered; firstInstance={FirstInstance}, newInstance={NewInstance}", isFirstInstance, allowMultiple);

        var services = new ServiceCollection();
        services.AddSingleton<FpsSampler>();
        services.AddSingleton<SystemMonitor>();
        services.AddSingleton<Database>();
        services.AddSingleton<OptimizerService>();
        services.AddSingleton<StartupManagerService>();
        services.AddSingleton<BackupService>();
        services.AddSingleton<CleanerService>();
        services.AddSingleton<DisplayControlService>();
        services.AddSingleton<ProcessManagerService>();
        services.AddSingleton<OverlayWindowService>();
        services.AddSingleton<DashboardViewModel>();
        services.AddSingleton<StatisticsViewModel>();
        services.AddSingleton<OptimizerViewModel>();
        services.AddSingleton<StartupManagerViewModel>();
        services.AddSingleton<BackupViewModel>();
        services.AddSingleton<CleanerViewModel>();
        services.AddSingleton<OverlayViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<DisplayControlViewModel>();
        services.AddSingleton<ProcessManagerViewModel>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();
        Services = services.BuildServiceProvider();
        Log.Information("Service provider ready");

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Error(args.ExceptionObject as Exception, "Unhandled");

        try
        {
            Services.GetRequiredService<Database>().Initialize();
            Log.Information("Database initialization completed");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Datenbank-Initialisierung fehlgeschlagen");
        }

        try
        {
            Log.Information("Resolving main window");
            var mainWindow = Services.GetRequiredService<MainWindow>();
            Log.Information("Main window constructed");
            mainWindow.Show();
            Log.Information("Main window shown");
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Main window startup failed");
            MessageBox.Show($"NexusOptimizer konnte nicht gestartet werden:\n\n{ex.Message}", "NexusOptimizer", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
        }
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
        DisplayControlService.RestoreSoftwareBrightness();
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
