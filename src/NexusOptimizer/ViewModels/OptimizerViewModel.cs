using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexusOptimizer.Models;
using NexusOptimizer.Services;

namespace NexusOptimizer.ViewModels;

public partial class OptimizerViewModel : ObservableObject
{
    private readonly OptimizerService _service;
    private bool _suppressItemChanges;

    public ObservableCollection<OptimizationItem> Items { get; }

    [ObservableProperty] private string _modeText = string.Empty;
    [ObservableProperty] private string _summary = "Bereit.";

    public OptimizerViewModel(OptimizerService service)
    {
        _service = service;
        Items = new ObservableCollection<OptimizationItem>(service.Items);
        ModeText = service.IsAdmin
            ? "Administrator – alle Tweaks verfügbar"
            : "Standardmodus – System-Tweaks benötigen Adminrechte";

        foreach (var item in Items)
        {
            item.IsAvailable = !item.NeedsAdmin || service.IsAdmin;
            item.IsEnabled = item.SupportsRollback && service.HasRollbackSnapshot(item.Key);
            item.PropertyChanged += OnItemPropertyChanged;
        }
    }

    private async void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppressItemChanges || e.PropertyName != nameof(OptimizationItem.IsEnabled)) return;
        if (sender is not OptimizationItem item) return;
        await ApplyItemAsync(item, item.IsEnabled);
    }

    [RelayCommand]
    private async Task ApplyAllAsync()
    {
        var answer = MessageBox.Show(
            "Es werden nur verfügbare, als niedrig eingestufte Schalter aktiviert. Einmalaktionen und Tweaks mit mittlerem Risiko bleiben außen vor.\n\nFortfahren?",
            "Sichere Tweaks anwenden", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        _suppressItemChanges = true;
        try
        {
            foreach (var item in Items)
            {
                if (!item.IsAvailable || item.IsEnabled || item.IsOneShot || !item.SupportsRollback || item.Risk != "Niedrig") continue;
                item.IsEnabled = true;
                await ApplyItemAsync(item, true);
            }
        }
        finally { _suppressItemChanges = false; }
    }

    [RelayCommand]
    private async Task ResetAllAsync()
    {
        var answer = MessageBox.Show(
            "Alle aktiven Optimizer-Schalter werden zurückgesetzt. Einmalaktionen wie RAM-Bereinigung oder Zwischenablage sind nicht rückgängig zu machen.",
            "Optimizer zurücksetzen", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        _suppressItemChanges = true;
        try
        {
            foreach (var item in Items)
            {
                if (!item.IsEnabled || !item.SupportsRollback) continue;
                item.IsEnabled = false;
                await ApplyItemAsync(item, false);
            }
        }
        finally { _suppressItemChanges = false; }

        Summary = "Gesicherte, reversible Tweaks wurden zurückgesetzt.";
    }

    [RelayCommand]
    private async Task RunOneShotAsync(OptimizationItem? item)
    {
        if (item is null || !item.IsAvailable || !item.IsOneShot || item.IsBusy) return;

        string detail = item.Key == "clipboard"
            ? "Der aktuelle Zwischenablage-Inhalt wird unwiderruflich gelöscht."
            : "Windows verwaltet freien RAM selbst. Das Trimmen kann Daten beim nächsten Zugriff erneut laden lassen und Spiele kurz ruckeln lassen.";
        var answer = MessageBox.Show($"{detail}\n\n{item.Title} jetzt ausführen?", item.Title,
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        bool available = item.IsAvailable;
        item.IsBusy = true;
        item.IsAvailable = false;
        item.Status = "Wird ausgeführt …";
        try
        {
            string result = await Task.Run(() => _service.Apply(item, true)).ConfigureAwait(true);
            item.Status = result;
            Summary = item.Title + ": " + result;
        }
        finally { item.IsAvailable = available; item.IsBusy = false; }
    }

    private async Task ApplyItemAsync(OptimizationItem item, bool enabled)
    {
        bool available = item.IsAvailable;
        item.IsBusy = true;
        item.IsAvailable = false;
        item.Status = "Wird angewendet …";
        try
        {
            string result = await Task.Run(() => _service.Apply(item, enabled)).ConfigureAwait(true);
            item.Status = result;
            Summary = item.Title + ": " + result;
            if (IsFailure(result))
            {
                bool wasSuppressed = _suppressItemChanges;
                _suppressItemChanges = true;
                item.IsEnabled = !enabled;
                _suppressItemChanges = wasSuppressed;
            }
        }
        finally { item.IsAvailable = available; item.IsBusy = false; }
    }

    private static bool IsFailure(string result) =>
        result.Contains("Fehler", StringComparison.OrdinalIgnoreCase) ||
        result.Contains("verweigert", StringComparison.OrdinalIgnoreCase) ||
        result.Contains("erforderlich", StringComparison.OrdinalIgnoreCase) ||
        result.Contains("unvollständig", StringComparison.OrdinalIgnoreCase) ||
        result.StartsWith("Kein ", StringComparison.OrdinalIgnoreCase) ||
        result.StartsWith("Keine ", StringComparison.OrdinalIgnoreCase) ||
        result.StartsWith("Unbekannt", StringComparison.OrdinalIgnoreCase) ||
        result.StartsWith("Einmalaktionen", StringComparison.OrdinalIgnoreCase) ||
        result.StartsWith("Vor der Änderung", StringComparison.OrdinalIgnoreCase);
}
