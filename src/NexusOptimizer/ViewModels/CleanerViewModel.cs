using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexusOptimizer.Services;

namespace NexusOptimizer.ViewModels;

public partial class CleanerViewModel : ObservableObject
{
    private readonly CleanerService _service;
    private IReadOnlyList<TempFileCandidate> _candidates = Array.Empty<TempFileCandidate>();

    [ObservableProperty] private string _summary = "Temp-Dateien werden gesucht …";
    [ObservableProperty] private string _status = "Nur Dateien, die seit mindestens sieben Tagen unverändert sind, werden angeboten.";
    [ObservableProperty] private bool _isBusy;

    public string TempPath => _service.TempPath;
    public bool CanRun => !IsBusy;
    public ObservableCollection<string> LargestFiles { get; } = new();

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanRun));

    public CleanerViewModel(CleanerService service)
    {
        _service = service;
        _ = RefreshAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            _candidates = await Task.Run(_service.Scan).ConfigureAwait(true);
            long total = _candidates.Sum(x => x.SizeBytes);
            Summary = $"{_candidates.Count:N0} alte Dateien · {CleanerService.FormatBytes(total)} verfügbar";
            LargestFiles.Clear();
            foreach (var file in _candidates.Take(5))
                LargestFiles.Add($"{Path.GetFileName(file.Path)} · {CleanerService.FormatBytes(file.SizeBytes)}");
            Status = _candidates.Count == 0
                ? "Keine geeigneten Dateien gefunden."
                : "Dateien bleiben zunächst unverändert; Löschen verschiebt sie in den Papierkorb.";
        }
        catch (Exception ex) { Status = "Temp-Ordner konnte nicht gelesen werden: " + ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task CleanAsync()
    {
        if (IsBusy || _candidates.Count == 0) return;
        long total = _candidates.Sum(x => x.SizeBytes);
        var answer = MessageBox.Show(
            $"{_candidates.Count:N0} Dateien ({CleanerService.FormatBytes(total)}) aus deinem Temp-Ordner werden in den Papierkorb verschoben. Windows- und App-Daten außerhalb dieses Ordners bleiben unberührt.\n\nFortfahren?",
            "Temporäre Dateien bereinigen", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        IsBusy = true;
        Status = "Dateien werden in den Papierkorb verschoben …";
        try
        {
            var result = await Task.Run(() => _service.MoveToRecycleBin(_candidates)).ConfigureAwait(true);
            Status = result.Message;
            IsBusy = false;
            await RefreshAsync();
        }
        finally { IsBusy = false; }
    }
}
