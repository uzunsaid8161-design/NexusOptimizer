using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using NexusOptimizer.Services;

namespace NexusOptimizer.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "NexusOptimizer";
    private readonly Database _database;

    public IReadOnlyList<string> MotionModes { get; } = ["Voll", "Reduziert", "Aus"];
    public string DataFolder => AppPaths.Root;
    public string DatabaseFile => AppPaths.DatabaseFile;

    [ObservableProperty] private string _motionMode = "Voll";
    [ObservableProperty] private double _animationSpeed = 1.0;
    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private string _startupStatus = string.Empty;
    [ObservableProperty] private string _status = "Einstellungen werden lokal gespeichert.";

    public string StartupButtonText => StartWithWindows ? "Autostart deaktivieren" : "Mit Windows starten";

    partial void OnStartWithWindowsChanged(bool value) => OnPropertyChanged(nameof(StartupButtonText));

    public SettingsViewModel(Database database)
    {
        _database = database;
        try
        {
            MotionMode = database.GetSetting("motion.level") switch
            {
                "Reduced" => "Reduziert",
                "Off" => "Aus",
                _ => "Voll"
            };
            if (double.TryParse(database.GetSetting("motion.speed"), NumberStyles.Float, CultureInfo.InvariantCulture, out double speed))
                AnimationSpeed = Math.Clamp(speed, 0.5, 2.0);
        }
        catch (Exception ex)
        {
            Status = "Datenbank nicht erreichbar; die App läuft mit Standardeinstellungen. " + ex.Message;
            Serilog.Log.Warning(ex, "Bewegungseinstellungen konnten beim Start nicht geladen werden");
        }

        MotionService.Current.Level = MotionMode switch
        {
            "Reduziert" => MotionLevel.Reduced,
            "Aus" => MotionLevel.Off,
            _ => MotionLevel.Full
        };
        MotionService.Current.SpeedFactor = AnimationSpeed;

        (StartWithWindows, StartupStatus) = ReadStartupState();
    }

    [RelayCommand]
    private void SaveSettings()
    {
        MotionLevel level = MotionMode switch
        {
            "Reduziert" => MotionLevel.Reduced,
            "Aus" => MotionLevel.Off,
            _ => MotionLevel.Full
        };

        MotionService.Current.Level = level;
        MotionService.Current.SpeedFactor = Math.Clamp(AnimationSpeed, 0.5, 2.0);
        try
        {
            _database.SetSetting("motion.level", level.ToString());
            _database.SetSetting("motion.speed", MotionService.Current.SpeedFactor.ToString(CultureInfo.InvariantCulture));
            Status = "Bewegungseinstellungen gespeichert und sofort angewendet.";
        }
        catch (Exception ex)
        {
            Status = "Datenbank nicht erreichbar; Bewegung gilt nur bis zum Schließen der App. " + ex.Message;
            Serilog.Log.Warning(ex, "Bewegungseinstellungen konnten nicht gespeichert werden");
        }
    }

    [RelayCommand]
    private void ToggleStartup()
    {
        try
        {
            string? executable = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(executable))
            {
                StartupStatus = "Der Programm-Pfad konnte nicht ermittelt werden.";
                return;
            }

            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
                ?? throw new UnauthorizedAccessException("Der Autostart-Schlüssel ist nicht verfügbar.");
            string? existing = key.GetValue(RunValue, null, RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString();

            if (StartWithWindows)
            {
                if (string.IsNullOrWhiteSpace(existing))
                {
                    StartWithWindows = false;
                    StartupStatus = "Der NexusOptimizer-Autostart war bereits entfernt.";
                    return;
                }
                if (!PointsToCurrentExecutable(existing, executable))
                {
                    StartupStatus = "Der vorhandene Eintrag verweist auf eine andere Installation und wurde nicht verändert.";
                    return;
                }

                key.DeleteValue(RunValue, throwOnMissingValue: false);
                StartWithWindows = false;
                StartupStatus = "NexusOptimizer startet nicht mehr automatisch mit Windows.";
                return;
            }

            if (!string.IsNullOrWhiteSpace(existing) && !PointsToCurrentExecutable(existing, executable))
            {
                StartupStatus = "Ein anderer NexusOptimizer-Autostart existiert bereits. Er wurde nicht überschrieben.";
                return;
            }

            key.SetValue(RunValue, $"\"{executable}\"", RegistryValueKind.String);
            StartWithWindows = true;
            StartupStatus = "NexusOptimizer startet nach der nächsten Anmeldung mit Windows.";
        }
        catch (Exception ex)
        {
            StartupStatus = "Autostart konnte nicht geändert werden: " + ex.Message;
        }
    }

    [RelayCommand]
    private void OpenDataFolder()
    {
        try
        {
            Directory.CreateDirectory(DataFolder);
            var startInfo = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            startInfo.ArgumentList.Add(DataFolder);
            Process.Start(startInfo);
        }
        catch (Exception ex) { Status = "Datenordner konnte nicht geöffnet werden: " + ex.Message; }
    }

    private static (bool Enabled, string Status) ReadStartupState()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            string? value = key?.GetValue(RunValue, null, RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString();
            if (string.IsNullOrWhiteSpace(value)) return (false, "Autostart ist ausgeschaltet.");

            string? executable = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (executable is not null && PointsToCurrentExecutable(value, executable))
                return (true, "Autostart ist für diese Installation eingeschaltet.");

            return (false, "Ein Autostart-Eintrag einer anderen Installation ist vorhanden und bleibt geschützt.");
        }
        catch (Exception ex) { return (false, "Autostart-Status konnte nicht gelesen werden: " + ex.Message); }
    }

    private static bool PointsToCurrentExecutable(string value, string executable)
    {
        string normalized = value.Trim().Trim('"');
        return string.Equals(Path.GetFullPath(normalized), Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase);
    }
}
