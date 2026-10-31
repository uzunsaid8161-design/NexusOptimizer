using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using NexusOptimizer.Services;

namespace NexusOptimizer.ViewModels;

public partial class StatisticsViewModel : ObservableObject
{
    private readonly FpsSampler _fps;
    private readonly SystemMonitor _system;
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _uptime = Stopwatch.StartNew();
    private readonly Queue<double> _recentFps = new();

    [ObservableProperty] private double _currentFps;
    [ObservableProperty] private double _averageFps;
    [ObservableProperty] private double _minimumFps;
    [ObservableProperty] private double _maximumFps;
    [ObservableProperty] private double _low1Fps;
    [ObservableProperty] private double _low01Fps;
    [ObservableProperty] private double _frameTimeMs;
    [ObservableProperty] private double _cpuPercent;
    [ObservableProperty] private double _gpuPercent;
    [ObservableProperty] private double _ramUsedGb;
    [ObservableProperty] private double _ramTotalGb;
    [ObservableProperty] private string _uptimeText = "00:00:00";
    [ObservableProperty] private string _cpuText = "CPU: —";
    [ObservableProperty] private string _gpuText = "GPU: —";
    [ObservableProperty] private string _ramText = "—";
    [ObservableProperty] private string _low01Text = "0,1 %-LOW: —";
    [ObservableProperty] private int _samplesRecorded;
    [ObservableProperty] private int _framesLastSecond;
    [ObservableProperty] private string _status = "Warte auf App-Compositor-Frames …";

    public ObservableCollection<double> FpsHistory { get; } = new();

    public StatisticsViewModel(FpsSampler fps, SystemMonitor system)
    {
        _fps = fps;
        _system = system;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
    }

    private void Refresh()
    {
        var sample = _system.Current;
        CpuPercent = sample.CpuPercent;
        GpuPercent = sample.GpuPercent;
        RamUsedGb = sample.RamUsedGb;
        RamTotalGb = sample.RamTotalGb;
        CpuText = $"CPU {CpuPercent:0}%";
        GpuText = $"GPU {GpuPercent:0}%";
        RamText = RamTotalGb > 0 ? $"{RamUsedGb:0.0} / {RamTotalGb:0.0} GB" : "—";
        FramesLastSecond = _fps.FramesLastSecond;
        FrameTimeMs = _fps.AverageFrameTimeMs;
        CurrentFps = _fps.CurrentFps;
        Low1Fps = _fps.LowPercentile(0.01);
        Low01Fps = _fps.LowPercentile(0.001);
        Low01Text = $"0,1 %-LOW: {Low01Fps:0}";

        var elapsed = _uptime.Elapsed;
        UptimeText = $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
        if (CurrentFps <= 0)
        {
            Status = "Warte auf App-Compositor-Frames. Ein laufendes Spiel wird hier nicht direkt gemessen.";
            return;
        }

        SamplesRecorded++;
        _recentFps.Enqueue(CurrentFps);
        FpsHistory.Add(CurrentFps);
        if (_recentFps.Count > 120) _recentFps.Dequeue();
        if (FpsHistory.Count > 120) FpsHistory.RemoveAt(0);
        MinimumFps = _recentFps.Min();
        MaximumFps = _recentFps.Max();
        AverageFps = _recentFps.Average();
        Status = "Live-Messung · 60-Sekunden-Fenster · FPS-Quelle: Nexus-App-Compositor";
    }
}
