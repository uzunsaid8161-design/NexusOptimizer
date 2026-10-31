using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexusOptimizer.Models;
using NexusOptimizer.Services;
using Serilog;

namespace NexusOptimizer.ViewModels;

public partial class ProcessManagerViewModel : ObservableObject
{
    private readonly ProcessManagerService _service;
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Background);
    private readonly Dictionary<int, HistoryAccumulator> _tracked = [];
    private readonly Dictionary<int, string> _lastSessionProcesses = [];
    private readonly string _statePath = Path.Combine(AppPaths.Root, "process-manager.json");
    private CancellationTokenSource? _samplingCancellation;
    private ProcessSystemSnapshot? _latest;
    private int _refreshing;
    private bool _isLoadingState = true;

    private sealed class HistoryAccumulator
    {
        public string Name = "";
        public int ProcessId;
        public DateTime Started;
        public double MaxCpu;
        public double RamMbSum;
        public int Samples;
    }

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _publisherFilter = "";
    [ObservableProperty] private string _statusFilter = "Alle";
    [ObservableProperty] private string _sortBy = "CPU";
    [ObservableProperty] private int _refreshSeconds = 2;
    [ObservableProperty] private bool _compactView;
    [ObservableProperty] private double _minimumCpu;
    [ObservableProperty] private double _minimumRamMb;
    [ObservableProperty] private bool _recordHistory;
    [ObservableProperty] private bool _gamingSessionActive;
    [ObservableProperty] private ProcessRowViewModel? _selectedProcess;
    [ObservableProperty] private WatchedProcessRecord? _selectedWatchedProcess;
    [ObservableProperty] private string _status = "Warte auf erste Prozessmessung …";
    [ObservableProperty] private string _detailsText = "Wähle einen Prozess aus der Übersicht.";
    [ObservableProperty] private string _processTreeText = "Prozessbaum und Dienste werden auf Anforderung gelesen.";
    [ObservableProperty] private string _cpuText = "—";
    [ObservableProperty] private string _ramText = "—";
    [ObservableProperty] private string _diskText = "—";
    [ObservableProperty] private string _networkText = "—";
    [ObservableProperty] private string _processCountText = "—";
    [ObservableProperty] private string _applicationCountText = "—";
    [ObservableProperty] private string _cpuTrend = "Warte auf Daten";
    [ObservableProperty] private string _ramTrend = "Warte auf Daten";

    public ObservableCollection<ProcessRowViewModel> Processes { get; } = [];
    public ObservableCollection<ProcessRowViewModel> VisibleProcesses { get; } = [];
    public ObservableCollection<WatchedProcessRecord> WatchedProcesses { get; } = [];
    public ObservableCollection<ProcessHistoryRecord> History { get; } = [];
    public ObservableCollection<string> GamingEvents { get; } = [];
    public int[] RefreshOptions { get; } = [1, 2, 5];
    public string[] SortOptions { get; } = ["CPU", "RAM", "Name", "Startzeit"];
    public string[] StatusOptions { get; } = ["Alle", "Läuft", "Beobachtet", "Geschützt"];
    public string EndButtonText => SelectedProcess?.IsProtected == true ? "Geschützt" : "Prozess beenden";
    public string GamingButtonText => GamingSessionActive ? "Gaming-Sitzung beenden" : "Gaming-Sitzung starten";
    public string HistoryButtonText => RecordHistory ? "Verlauf: Ein" : "Verlauf: Aus";
    public string ResultSummary => _latest == null ? "" : $"{VisibleProcesses.Count:N0} angezeigt · {_latest.Processes.Count:N0} Prozesse · Aktualisiert {DateTime.Now:HH:mm:ss}";

    public ProcessManagerViewModel(ProcessManagerService service)
    {
        _service = service;
        LoadState();
        _isLoadingState = false;
        _timer.Interval = TimeSpan.FromSeconds(RefreshSeconds);
        _timer.Tick += async (_, _) => await RefreshAsync();
    }

    public void StartMonitoring()
    {
        if (_timer.IsEnabled) return;
        _samplingCancellation?.Dispose();
        _samplingCancellation = new CancellationTokenSource();
        _timer.Interval = TimeSpan.FromSeconds(RefreshSeconds);
        _timer.Start();
        _ = RefreshAsync();
    }

    public void StopMonitoring()
    {
        _timer.Stop();
        _samplingCancellation?.Cancel();
    }

    private async Task RefreshAsync()
    {
        var token = _samplingCancellation?.Token ?? CancellationToken.None;
        if (Interlocked.Exchange(ref _refreshing, 1) != 0) return;
        try
        {
            var snapshot = await Task.Run(() => _service.Sample(token), token);
            token.ThrowIfCancellationRequested();
            _latest = snapshot;
            UpdateMetrics(snapshot);
            UpdateProcessRows(snapshot);
            UpdateGamingEvents(snapshot);
            UpdateHistory(snapshot);
            Status = $"Live · {snapshot.Processes.Count:N0} Prozesse · Aktualisierung alle {RefreshSeconds} s · Lesezugriff ohne Adminrechte";
            OnPropertyChanged(nameof(ResultSummary));
            OnPropertyChanged(nameof(EndButtonText));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Status = "Prozessliste konnte nicht aktualisiert werden: " + ex.Message;
            Log.Warning(ex, "Process Manager refresh failed");
        }
        finally { Interlocked.Exchange(ref _refreshing, 0); }
    }

    private void UpdateMetrics(ProcessSystemSnapshot s)
    {
        CpuText = $"{s.CpuPercent:0}%";
        RamText = $"{s.RamUsedGb:0.0} / {s.RamTotalGb:0.0} GB";
        DiskText = FormatRate(s.DiskBytesPerSecond);
        NetworkText = FormatRate(s.NetworkBytesPerSecond);
        ProcessCountText = $"{s.Processes.Count:N0}";
        ApplicationCountText = $"{s.ActiveApplications:N0}";
        CpuTrend = MakeTrend(s.CpuPercent, CpuTrend);
        RamTrend = MakeTrend(s.RamTotalGb > 0 ? s.RamUsedGb / s.RamTotalGb * 100 : 0, RamTrend);
    }

    private static string MakeTrend(double value, string old)
    {
        const string levels = "▁▂▃▄▅▆▇█";
        string source = old is "Warte auf Daten" ? "" : old;
        int level = Math.Clamp((int)Math.Round(Math.Clamp(value, 0, 100) / 100 * (levels.Length - 1)), 0, levels.Length - 1);
        return (source + levels[level])[^48..];
    }

    private void UpdateProcessRows(ProcessSystemSnapshot snapshot)
    {
        var watched = WatchedProcesses.Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var byId = Processes.ToDictionary(x => x.ProcessId);
        var alive = new HashSet<int>();
        foreach (var item in snapshot.Processes)
        {
            alive.Add(item.ProcessId);
            bool isWatched = watched.Contains(item.Name);
            if (!byId.TryGetValue(item.ProcessId, out var row))
            {
                row = new ProcessRowViewModel();
                Processes.Add(row);
            }
            row.Update(item, isWatched);
        }
        foreach (var stale in Processes.Where(x => !alive.Contains(x.ProcessId)).ToArray()) Processes.Remove(stale);
        RebuildVisible();
        if (SelectedProcess != null && !Processes.Contains(SelectedProcess)) SelectedProcess = null;
        else if (SelectedProcess != null) UpdateSelectedDetails();
    }

    private void RebuildVisible()
    {
        IEnumerable<ProcessRowViewModel> rows = Processes;
        string query = SearchText.Trim();
        if (query.Length > 0)
            rows = rows.Where(x => x.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                   x.ProcessId.ToString().Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                   x.Publisher.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                   (x.FilePath?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                                   x.Status.Contains(query, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(PublisherFilter))
            rows = rows.Where(x => x.Publisher.Contains(PublisherFilter, StringComparison.OrdinalIgnoreCase));
        if (MinimumCpu > 0) rows = rows.Where(x => x.CpuPercent >= MinimumCpu);
        if (MinimumRamMb > 0) rows = rows.Where(x => x.WorkingSetBytes >= MinimumRamMb * 1024 * 1024);
        rows = StatusFilter switch
        {
            "Beobachtet" => rows.Where(x => x.IsWatched),
            "Geschützt" => rows.Where(x => x.IsProtected),
            _ => rows
        };
        rows = SortBy switch
        {
            "RAM" => rows.OrderByDescending(x => x.WorkingSetBytes),
            "Name" => rows.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase),
            "Startzeit" => rows.OrderByDescending(x => x.StartedAt),
            _ => rows.OrderByDescending(x => x.CpuPercent)
        };
        VisibleProcesses.Clear();
        foreach (var row in rows) VisibleProcesses.Add(row);
        OnPropertyChanged(nameof(ResultSummary));
    }

    private void UpdateSelectedDetails()
    {
        if (SelectedProcess is not { } p) return;
        DetailsText = $"{p.Name} · PID {p.ProcessId}\nPfad: {p.PathText}\nHerausgeber: {p.Publisher}\nDigitale Signatur: {p.Signature}\nStart: {p.StartText}\nArbeitsspeicher: {p.MemoryText} · CPU: {p.CpuText} · Threads: {p.ThreadCount}\nI/O (Lesen/Schreiben): {p.IoText}\nStatus: {p.Status}" +
                      (p.IsProtected ? "\nWindows-/Sicherheitsprozess: Beenden ist gesperrt." : "");
        OnPropertyChanged(nameof(EndButtonText));
    }

    private void UpdateGamingEvents(ProcessSystemSnapshot snapshot)
    {
        if (!GamingSessionActive) return;
        var current = snapshot.Processes.ToDictionary(x => x.ProcessId, x => x.Name);
        foreach (var item in current.Where(x => !_lastSessionProcesses.ContainsKey(x.Key))) AddGamingEvent($"+ {item.Value} · PID {item.Key}");
        foreach (var item in _lastSessionProcesses.Where(x => !current.ContainsKey(x.Key))) AddGamingEvent($"− {item.Value} · PID {item.Key}");
        _lastSessionProcesses.Clear();
        foreach (var pair in current) _lastSessionProcesses[pair.Key] = pair.Value;
    }

    private void UpdateHistory(ProcessSystemSnapshot snapshot)
    {
        if (!RecordHistory || WatchedProcesses.Count == 0) return;
        var names = WatchedProcesses.Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var current = snapshot.Processes.Where(x => names.Contains(x.Name)).ToDictionary(x => x.ProcessId);
        foreach (var item in current.Values)
        {
            if (!_tracked.TryGetValue(item.ProcessId, out var state))
            {
                state = new HistoryAccumulator { Name = item.Name, ProcessId = item.ProcessId, Started = item.StartedAt ?? DateTime.Now };
                _tracked[item.ProcessId] = state;
            }
            state.MaxCpu = Math.Max(state.MaxCpu, item.CpuPercent);
            state.RamMbSum += item.WorkingSetBytes / 1024d / 1024;
            state.Samples++;
        }
        foreach (int id in _tracked.Keys.Where(id => !current.ContainsKey(id)).ToArray())
        {
            var ended = _tracked[id];
            History.Insert(0, new ProcessHistoryRecord
            {
                Name = ended.Name, ProcessId = ended.ProcessId, StartedAt = ended.Started,
                EndedAt = DateTime.Now, MaximumCpuPercent = ended.MaxCpu,
                AverageRamMb = ended.Samples == 0 ? 0 : ended.RamMbSum / ended.Samples
            });
            _tracked.Remove(id);
            while (History.Count > 300) History.RemoveAt(History.Count - 1);
            SaveState();
        }
    }

    private static string FormatRate(double bytes) => bytes >= 1024 * 1024
        ? $"{bytes / 1024 / 1024:0.0} MB/s"
        : $"{bytes / 1024:0} KB/s";

    private void AddGamingEvent(string entry)
    {
        GamingEvents.Insert(0, $"{DateTime.Now:HH:mm:ss}  {entry}");
        while (GamingEvents.Count > 60) GamingEvents.RemoveAt(GamingEvents.Count - 1);
    }

    private void LoadState()
    {
        try
        {
            if (!File.Exists(_statePath)) return;
            var state = JsonSerializer.Deserialize<ProcessManagerState>(File.ReadAllText(_statePath));
            if (state == null) return;
            RecordHistory = state.RecordHistory;
            foreach (string name in state.WatchedNames.Distinct(StringComparer.OrdinalIgnoreCase)) WatchedProcesses.Add(new WatchedProcessRecord { Name = name });
            foreach (var item in state.History.OrderByDescending(x => x.EndedAt).Take(300)) History.Add(item);
        }
        catch (Exception ex) { Log.Warning(ex, "Process Manager state load failed"); }
    }

    private void SaveState()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            var state = new ProcessManagerState
            {
                RecordHistory = RecordHistory,
                WatchedNames = WatchedProcesses.Select(x => x.Name).ToList(),
                History = History.ToList()
            };
            File.WriteAllText(_statePath, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { Log.Warning(ex, "Process Manager state save failed"); }
    }

    partial void OnSearchTextChanged(string value) => RebuildVisible();
    partial void OnPublisherFilterChanged(string value) => RebuildVisible();
    partial void OnStatusFilterChanged(string value) => RebuildVisible();
    partial void OnSortByChanged(string value) => RebuildVisible();
    partial void OnMinimumCpuChanged(double value) => RebuildVisible();
    partial void OnMinimumRamMbChanged(double value) => RebuildVisible();
    partial void OnRefreshSecondsChanged(int value)
    {
        if (value is not (1 or 2 or 5)) return;
        _timer.Interval = TimeSpan.FromSeconds(value);
    }
    partial void OnCompactViewChanged(bool value) => OnPropertyChanged(nameof(CompactView));
    partial void OnRecordHistoryChanged(bool value)
    {
        if (_isLoadingState) return;
        if (!value) _tracked.Clear();
        SaveState();
        OnPropertyChanged(nameof(HistoryButtonText));
    }
    partial void OnGamingSessionActiveChanged(bool value)
    {
        _lastSessionProcesses.Clear();
        AddGamingEvent(value ? "Gaming-Sitzung gestartet" : "Gaming-Sitzung beendet");
        OnPropertyChanged(nameof(GamingButtonText));
    }
    partial void OnSelectedProcessChanged(ProcessRowViewModel? value) => UpdateSelectedDetails();

    [RelayCommand] private Task Refresh() => RefreshAsync();

    [RelayCommand]
    private void AddWatchedProcess()
    {
        if (SelectedProcess == null) { Status = "Wähle zuerst einen Prozess aus."; return; }
        if (WatchedProcesses.Any(x => x.Name.Equals(SelectedProcess.Name, StringComparison.OrdinalIgnoreCase)))
        { Status = $"{SelectedProcess.Name} wird bereits beobachtet."; return; }
        WatchedProcesses.Add(new WatchedProcessRecord { Name = SelectedProcess.Name });
        SaveState();
        RebuildVisible();
        Status = $"{SelectedProcess.Name} zum Gaming-Profil hinzugefügt.";
    }

    [RelayCommand]
    private void RemoveWatchedProcess()
    {
        if (SelectedWatchedProcess == null) return;
        WatchedProcesses.Remove(SelectedWatchedProcess);
        _tracked.Clear();
        SaveState();
        RebuildVisible();
    }

    [RelayCommand]
    private void ToggleGamingSession() => GamingSessionActive = !GamingSessionActive;

    [RelayCommand]
    private void ToggleMark()
    {
        if (SelectedProcess == null) return;
        SelectedProcess.IsMarked = !SelectedProcess.IsMarked;
        Status = SelectedProcess.IsMarked ? "Prozess in der Übersicht markiert." : "Markierung entfernt.";
    }

    [RelayCommand]
    private void CopyProcessInfo()
    {
        if (SelectedProcess == null) return;
        try { Clipboard.SetText(DetailsText); Status = "Prozessinformationen kopiert."; }
        catch (Exception ex) { Status = "Kopieren fehlgeschlagen: " + ex.Message; }
    }

    [RelayCommand]
    private async Task LoadProcessTree()
    {
        if (SelectedProcess == null) return;
        int id = SelectedProcess.ProcessId;
        ProcessTreeText = "Prozessbaum wird gelesen …";
        try { ProcessTreeText = await Task.Run(() => _service.GetProcessRelations(id)); }
        catch (Exception ex) { ProcessTreeText = "Prozessbaum nicht verfügbar: " + ex.Message; }
    }

    [RelayCommand]
    private void OpenProcessPath()
    {
        if (string.IsNullOrWhiteSpace(SelectedProcess?.FilePath)) { Status = "Der Prozesspfad ist wegen fehlender Zugriffsrechte nicht verfügbar."; return; }
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{SelectedProcess.FilePath}\"") { UseShellExecute = false }); }
        catch (Exception ex) { Status = "Pfad konnte nicht geöffnet werden: " + ex.Message; }
    }

    [RelayCommand]
    private void ShowFileProperties()
    {
        if (string.IsNullOrWhiteSpace(SelectedProcess?.FilePath)) { Status = "Der Prozesspfad ist nicht verfügbar."; return; }
        try { Process.Start(new ProcessStartInfo(SelectedProcess.FilePath) { UseShellExecute = true, Verb = "properties" }); }
        catch (Exception ex) { Status = "Dateieigenschaften konnten nicht geöffnet werden: " + ex.Message; }
    }

    [RelayCommand]
    private async Task EndProcess()
    {
        if (SelectedProcess is not { } selected) { Status = "Wähle zuerst einen Prozess aus."; return; }
        if (selected.IsProtected) { Status = "Dieser Windows- oder Sicherheitsprozess ist geschützt."; return; }
        string prompt = $"Prozess {selected.Name} (PID {selected.ProcessId}) beenden?\n\nUngespeicherte Daten können verloren gehen. Es wird nur dieser Prozess beendet, nicht der Prozessbaum.";
        if (MessageBox.Show(prompt, "Prozess beenden", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        try
        {
            using var process = Process.GetProcessById(selected.ProcessId);
            process.Kill(entireProcessTree: false);
            Log.Information("User ended process {Name} PID {Pid}", selected.Name, selected.ProcessId);
            Status = $"{selected.Name} wurde beendet.";
            await Task.Delay(350);
            await RefreshAsync();
        }
        catch (Exception ex) { Status = "Prozess konnte nicht beendet werden: " + ex.Message; }
    }

    [RelayCommand]
    private void ClearHistory()
    {
        if (History.Count == 0 && GamingEvents.Count == 0) return;
        if (MessageBox.Show("Den lokalen Prozessverlauf und Gaming-Sitzungsereignisse löschen?", "Verlauf löschen", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        History.Clear();
        GamingEvents.Clear();
        _tracked.Clear();
        SaveState();
        Status = "Lokaler Verlauf gelöscht.";
    }
}
