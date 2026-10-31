using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexusOptimizer.Services;

namespace NexusOptimizer.ViewModels;

public partial class DashboardViewModel : ObservableObject
{
    private readonly FpsSampler _fps;
    private readonly MemoryOptimizer _memory = new();
    private readonly DispatcherTimer _timer;
    private readonly List<double> _history = new();

    [ObservableProperty] private string _freeRamText = "—";
    [ObservableProperty] private string _memoryStatus = "Misst vor und nach dem Freigeben.";
    [ObservableProperty] private bool _isFreeingMemory;

    [ObservableProperty] private double _fpsValue;
    [ObservableProperty] private double _minValue;
    [ObservableProperty] private double _avgValue;
    [ObservableProperty] private double _maxValue;
    [ObservableProperty] private double _low1Value;
    [ObservableProperty] private double _low01Value;
    [ObservableProperty] private string _frameTimeText = "—";
    [ObservableProperty] private double _cpuPercent;
    [ObservableProperty] private double _gpuPercent;
    [ObservableProperty] private double _ramPercent;
    [ObservableProperty] private string _ramText = "—";
    [ObservableProperty] private string _statusText = "Monitoring aktiv";
    [ObservableProperty] private bool _isMonitoring = true;

    public ObservableCollection<double> FpsHistory { get; } = new();

    public DashboardViewModel(FpsSampler fps, SystemMonitor system)
    {
        _fps = fps;
        system.Updated += OnSystemUpdated;

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
    }

    private void OnSystemUpdated(SystemSample sample)
    {
        // UI-Thread: Werte gebündelt übernehmen
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            CpuPercent = sample.CpuPercent;
            GpuPercent = sample.GpuPercent;
            RamPercent = sample.RamPercent;
            RamText = $"{sample.RamUsedGb:0,0} / {sample.RamTotalGb:0,0} GB";
            FreeRamText = $"{Math.Max(0, sample.RamTotalGb - sample.RamUsedGb):0.0} GB frei";
        });
    }

    /// <summary>
    /// Gibt Speicher frei, soweit es die Rechte zulassen, und zeigt das echte Delta.
    /// Läuft im Hintergrund, damit die UI nicht blockiert.
    /// </summary>
    [RelayCommand]
    private async Task FreeMemoryAsync()
    {
        if (IsFreeingMemory) return;

        IsFreeingMemory = true;
        MemoryStatus = "Gebe Speicher frei (sanft) …";

        try
        {
            var result = await Task.Run(() => _memory.Release(intensive: false)).ConfigureAwait(true);
            ApplyMemoryResult(result);
        }
        finally
        {
            IsFreeingMemory = false;
        }
    }

    /// <summary>
    /// Intensiv: trimmt zusätzlich die Working Sets anderer Benutzerprozesse.
    /// Kann Spiele kurz ruckeln lassen, weil Seiten nachgeladen werden müssen.
    /// </summary>
    [RelayCommand]
    private async Task FreeMemoryDeepAsync()
    {
        if (IsFreeingMemory) return;

        IsFreeingMemory = true;
        MemoryStatus = "Trimme Working Sets aller erreichbaren Prozesse …";

        try
        {
            var result = await Task.Run(() => _memory.Release(intensive: true)).ConfigureAwait(true);
            ApplyMemoryResult(result);
        }
        finally
        {
            IsFreeingMemory = false;
        }
    }

    private void ApplyMemoryResult(MemoryReleaseResult result)
    {
        FreeRamText = $"{result.AvailableAfterGb:0.0} GB frei" +
                      (result.WorkingSetFreedMb > 0 ? $" · {result.WorkingSetFreedMb} MB gelöst" : string.Empty);
        MemoryStatus = result.Message;
    }

    private void Refresh()
    {
        double value = _fps.CurrentFps;
        if (value < 10 || value > 300) return;

        FpsValue = value;
        FrameTimeText = (1000.0 / value).ToString("0,00") + " ms";

        _history.Add(value);
        if (_history.Count > 120) _history.RemoveAt(0);

        MinValue = _history.Min();
        MaxValue = _history.Max();
        AvgValue = _history.Average();
        Low1Value = _fps.LowPercentile(0.01);
        Low01Value = _fps.LowPercentile(0.001);

        FpsHistory.Add(value);
        if (FpsHistory.Count > 60) FpsHistory.RemoveAt(0);

        IsMonitoring = true;
        StatusText = "Monitoring aktiv · 5 Samples/s";
    }
}
