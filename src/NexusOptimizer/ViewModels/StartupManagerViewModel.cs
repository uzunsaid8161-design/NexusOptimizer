using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexusOptimizer.Models;
using NexusOptimizer.Services;

namespace NexusOptimizer.ViewModels;

public partial class StartupManagerViewModel : ObservableObject
{
    private readonly StartupManagerService _service;

    public ObservableCollection<StartupEntry> Entries { get; } = new();
    public ObservableCollection<StartupEntry> VisibleEntries { get; } = new();
    public IReadOnlyList<string> Filters { get; } = ["Alle", "Aktiv", "Deaktiviert", "Verwaltbar"];

    [ObservableProperty] private string _summary = "Autostart wird eingelesen …";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _permissionText = string.Empty;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private string _filter = "Alle";
    [ObservableProperty] private string _emptyMessage = "Autostart wird eingelesen …";
    [ObservableProperty] private bool _hasVisibleEntries;

    partial void OnSearchTextChanged(string value) => ApplyFilter();
    partial void OnFilterChanged(string value) => ApplyFilter();

    public StartupManagerViewModel(StartupManagerService service)
    {
        _service = service;
        PermissionText = service.IsAdmin
            ? "Systemeinträge sind lesbar und änderbar. Jede Deaktivierung wird lokal gesichert."
            : "Systemeinträge sind sichtbar. Änderungen daran benötigen Administratorrechte; Benutzer-Einträge bleiben verwaltbar.";
        _ = RefreshAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var result = await Task.Run(_service.Scan).ConfigureAwait(true);
            Entries.Clear();
            foreach (var entry in result) Entries.Add(entry);
            int enabled = result.Count(x => x.IsEnabled);
            int disabled = result.Count(x => !x.IsEnabled);
            int readOnly = result.Count(x => !x.CanManage);
            Summary = $"{result.Count} Einträge · {enabled} aktiv · {disabled} deaktiviert und gesichert · {readOnly} nur lesbar";
            ApplyFilter();
        }
        catch (Exception ex)
        {
            Summary = "Autostart konnte nicht eingelesen werden: " + ex.Message;
            Entries.Clear();
            ApplyFilter();
        }
        finally { IsBusy = false; }
    }

    private void ApplyFilter()
    {
        IEnumerable<StartupEntry> filtered = Entries;
        filtered = Filter switch
        {
            "Aktiv" => filtered.Where(x => x.IsEnabled),
            "Deaktiviert" => filtered.Where(x => !x.IsEnabled),
            "Verwaltbar" => filtered.Where(x => x.CanManage),
            _ => filtered
        };

        string query = SearchText.Trim();
        if (query.Length > 0)
        {
            filtered = filtered.Where(x =>
                x.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                x.Location.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                x.Command.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                x.Availability.Contains(query, StringComparison.CurrentCultureIgnoreCase));
        }

        var visible = filtered.OrderByDescending(x => x.IsEnabled)
            .ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        VisibleEntries.Clear();
        foreach (var entry in visible) VisibleEntries.Add(entry);
        HasVisibleEntries = visible.Count > 0;
        EmptyMessage = Entries.Count == 0
            ? "Keine Autostart-Einträge gefunden."
            : "Keine Treffer. Ändere die Suche oder den Filter.";
    }

    [RelayCommand]
    private async Task DisableAsync(StartupEntry? entry)
    {
        if (entry is null || !entry.CanManage || entry.IsBusy) return;
        string runTiming = entry.RegistryPath.EndsWith("RunOnce", StringComparison.OrdinalIgnoreCase)
            ? "beim nächsten Windows-Start einmalig"
            : "bei jedem Windows-Start";
        var answer = MessageBox.Show(
            $"{entry.Name} wird {runTiming} nicht mehr ausgeführt. NexusOptimizer sichert den Eintrag und kann ihn hier wiederherstellen.\n\nQuelle: {entry.Location}\n\nFortfahren?",
            "Autostart deaktivieren", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        entry.IsBusy = true;
        try
        {
            var result = await Task.Run(() => _service.SetEnabled(entry, false)).ConfigureAwait(true);
            Summary = result.Message;
            await RefreshAsync();
        }
        finally { entry.IsBusy = false; }
    }

    [RelayCommand]
    private async Task EnableAsync(StartupEntry? entry)
    {
        if (entry is null || !entry.CanManage || entry.IsBusy) return;
        var answer = MessageBox.Show(
            $"{entry.Name} wird aktiviert und startet künftig wieder mit Windows.\n\nFortfahren?",
            "Autostart aktivieren", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        entry.IsBusy = true;
        try
        {
            var result = await Task.Run(() => _service.SetEnabled(entry, true)).ConfigureAwait(true);
            Summary = result.Message;
            await RefreshAsync();
        }
        finally { entry.IsBusy = false; }
    }
}
