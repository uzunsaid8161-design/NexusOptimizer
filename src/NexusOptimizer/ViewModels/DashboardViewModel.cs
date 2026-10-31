using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using NexusOptimizer.Services;

namespace NexusOptimizer.ViewModels;

public partial class DashboardViewModel : ObservableObject
{
    private readonly FpsSampler _fps;
    private readonly DispatcherTimer _timer;
    private readonly List<double> _history = new();

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
        });
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
