using CommunityToolkit.Mvvm.ComponentModel;

namespace NexusOptimizer.Models;

public sealed record ProcessSnapshot(
    int ProcessId,
    int ParentProcessId,
    string Name,
    double CpuPercent,
    long WorkingSetBytes,
    double IoReadBytesPerSecond,
    double IoWriteBytesPerSecond,
    string Status,
    string? FilePath,
    string Publisher,
    string Signature,
    DateTime? StartedAt,
    int ThreadCount,
    bool IsProtected);

public sealed record ProcessSystemSnapshot(
    IReadOnlyList<ProcessSnapshot> Processes,
    double CpuPercent,
    double RamUsedGb,
    double RamTotalGb,
    double DiskBytesPerSecond,
    double NetworkBytesPerSecond,
    int ActiveApplications,
    DateTime SampledAt);

public partial class ProcessRowViewModel : ObservableObject
{
    [ObservableProperty] private int _processId;
    [ObservableProperty] private int _parentProcessId;
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private double _cpuPercent;
    [ObservableProperty] private long _workingSetBytes;
    [ObservableProperty] private double _ioReadBytesPerSecond;
    [ObservableProperty] private double _ioWriteBytesPerSecond;
    [ObservableProperty] private string _status = "Läuft";
    [ObservableProperty] private string? _filePath;
    [ObservableProperty] private string _publisher = "Nicht verfügbar";
    [ObservableProperty] private string _signature = "Nicht verfügbar";
    [ObservableProperty] private DateTime? _startedAt;
    [ObservableProperty] private int _threadCount;
    [ObservableProperty] private bool _isProtected;
    [ObservableProperty] private bool _isMarked;
    [ObservableProperty] private bool _isWatched;

    public string MemoryText => WorkingSetBytes >= 1024L * 1024 * 1024
        ? $"{WorkingSetBytes / 1024d / 1024 / 1024:0.00} GB"
        : $"{WorkingSetBytes / 1024d / 1024:0} MB";
    public string IoText => $"R {FormatRate(IoReadBytesPerSecond)} · W {FormatRate(IoWriteBytesPerSecond)}";
    public string CpuText => $"{CpuPercent:0.0}%";
    public string StartText => StartedAt?.ToString("dd.MM.yyyy HH:mm:ss") ?? "Nicht verfügbar";
    public string PathText => string.IsNullOrWhiteSpace(FilePath) ? "Zugriff nicht verfügbar" : FilePath;

    public void Update(ProcessSnapshot snapshot, bool watched)
    {
        ProcessId = snapshot.ProcessId;
        ParentProcessId = snapshot.ParentProcessId;
        Name = snapshot.Name;
        CpuPercent = snapshot.CpuPercent;
        WorkingSetBytes = snapshot.WorkingSetBytes;
        IoReadBytesPerSecond = snapshot.IoReadBytesPerSecond;
        IoWriteBytesPerSecond = snapshot.IoWriteBytesPerSecond;
        Status = snapshot.Status;
        FilePath = snapshot.FilePath;
        Publisher = snapshot.Publisher;
        Signature = snapshot.Signature;
        StartedAt = snapshot.StartedAt;
        ThreadCount = snapshot.ThreadCount;
        IsProtected = snapshot.IsProtected;
        IsWatched = watched;
        OnPropertyChanged(nameof(MemoryText));
        OnPropertyChanged(nameof(IoText));
        OnPropertyChanged(nameof(CpuText));
        OnPropertyChanged(nameof(StartText));
        OnPropertyChanged(nameof(PathText));
    }

    private static string FormatRate(double bytes) => bytes >= 1024 * 1024
        ? $"{bytes / 1024 / 1024:0.0} MB/s"
        : $"{bytes / 1024:0} KB/s";
}

public sealed class WatchedProcessRecord
{
    public string Name { get; set; } = "";
    public string DisplayName => Name;
}

public sealed class ProcessHistoryRecord
{
    public string Name { get; set; } = "";
    public int ProcessId { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime EndedAt { get; set; }
    public double MaximumCpuPercent { get; set; }
    public double AverageRamMb { get; set; }
    public double DurationMinutes => Math.Max(0, (EndedAt - StartedAt).TotalMinutes);
}

public sealed class ProcessManagerState
{
    public bool RecordHistory { get; set; }
    public List<string> WatchedNames { get; set; } = [];
    public List<ProcessHistoryRecord> History { get; set; } = [];
}
