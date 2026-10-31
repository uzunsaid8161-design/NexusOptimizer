using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexusOptimizer.Services;

namespace NexusOptimizer.ViewModels;

public partial class OverlayViewModel : ObservableObject
{
    private readonly Database _database;
    private readonly FpsSampler _fps;
    private readonly SystemMonitor _system;
    private readonly OverlayWindowService _windowService;
    private readonly DispatcherTimer _timer;

    [ObservableProperty] private bool _showFps = true;
    [ObservableProperty] private bool _showCpu = true;
    [ObservableProperty] private bool _showGpu;
    [ObservableProperty] private bool _showRam = true;
    [ObservableProperty] private double _overlayOpacity = 0.94;
    [ObservableProperty] private bool _overlayEnabled;
    [ObservableProperty] private string _fpsText = "—";
    [ObservableProperty] private string _cpuText = "—";
    [ObservableProperty] private string _gpuText = "—";
    [ObservableProperty] private string _ramText = "—";
    [ObservableProperty] private string _status = "Overlay ist aus. Die Anzeige nutzt aktuelle lokale Messwerte.";

    public string ToggleButtonText => OverlayEnabled ? "Overlay stoppen" : "Overlay starten";

    partial void OnOverlayEnabledChanged(bool value) => OnPropertyChanged(nameof(ToggleButtonText));

    public OverlayViewModel(Database database, FpsSampler fps, SystemMonitor system, OverlayWindowService windowService)
    {
        _database = database;
        _fps = fps;
        _system = system;
        _windowService = windowService;

        try
        {
            ShowFps = ReadBool("overlay.fps", true);
            ShowCpu = ReadBool("overlay.cpu", true);
            ShowGpu = ReadBool("overlay.gpu", false);
            ShowRam = ReadBool("overlay.ram", true);
            if (double.TryParse(database.GetSetting("overlay.opacity"), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double opacity))
                OverlayOpacity = Math.Clamp(opacity, 0.55, 1.0);
        }
        catch (Exception ex)
        {
            Status = "Gespeicherte Overlay-Werte nicht erreichbar; Standardwerte sind aktiv. " + ex.Message;
            Serilog.Log.Warning(ex, "Overlay-Einstellungen konnten beim Start nicht geladen werden");
        }

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) => RefreshReadings();
        _timer.Start();
        RefreshReadings();
    }

    [RelayCommand]
    private void ToggleOverlay()
    {
        if (OverlayEnabled)
        {
            OverlayEnabled = false;
            _windowService.Hide();
            Status = "Overlay ausgeschaltet.";
            return;
        }

        try
        {
            _windowService.Show(this, OnOverlayClosed);
            OverlayEnabled = true;
            Status = "Overlay aktiv · click-through · oben links · kein Fokuswechsel.";
        }
        catch (Exception ex) { Status = "Overlay konnte nicht gestartet werden: " + ex.Message; }
    }

    [RelayCommand]
    private void SaveLayout()
    {
        try
        {
            _database.SetSetting("overlay.fps", ShowFps.ToString());
            _database.SetSetting("overlay.cpu", ShowCpu.ToString());
            _database.SetSetting("overlay.gpu", ShowGpu.ToString());
            _database.SetSetting("overlay.ram", ShowRam.ToString());
            _database.SetSetting("overlay.opacity", OverlayOpacity.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Status = "Overlay-Layout lokal gespeichert.";
        }
        catch (Exception ex)
        {
            Status = "Overlay-Layout konnte nicht gespeichert werden: " + ex.Message;
            Serilog.Log.Warning(ex, "Overlay-Layout konnte nicht gespeichert werden");
        }
    }

    private void RefreshReadings()
    {
        double fps = _fps.CurrentFps;
        FpsText = fps > 0 ? fps.ToString("0.0") : "—";

        var sample = _system.Current;
        CpuText = $"{sample.CpuPercent:0}%";
        GpuText = sample.GpuPercent > 0 ? $"{sample.GpuPercent:0}%" : "—";
        RamText = sample.RamTotalGb > 0
            ? $"{sample.RamUsedGb:0.0} / {sample.RamTotalGb:0.0} GB"
            : "—";
    }

    private bool ReadBool(string key, bool fallback) =>
        bool.TryParse(_database.GetSetting(key), out bool value) ? value : fallback;

    private void OnOverlayClosed()
    {
        if (!OverlayEnabled) return;
        OverlayEnabled = false;
        Status = "Overlay geschlossen.";
    }
}
