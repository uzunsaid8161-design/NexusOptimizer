using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using NexusOptimizer.Services;

namespace NexusOptimizer.ViewModels;

public partial class BackupViewModel : ObservableObject
{
    private readonly BackupService _service;

    [ObservableProperty] private string _status = "Sicherungen enthalten Sitzungen, FPS-Daten, Einstellungen und das Optimierungsprotokoll.";
    [ObservableProperty] private bool _isBusy;

    public bool CanRun => !IsBusy;

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanRun));

    public string DatabasePath => _service.DatabasePath;
    public string BackupDirectory => _service.BackupDirectory;

    public BackupViewModel(BackupService service) => _service = service;

    [RelayCommand]
    private async Task CreateBackupAsync()
    {
        if (IsBusy) return;
        var dialog = new SaveFileDialog
        {
            Title = "NexusOptimizer-Datenbank sichern",
            Filter = "NexusOptimizer-Datenbank (*.db)|*.db",
            DefaultExt = ".db",
            AddExtension = true,
            FileName = $"NexusOptimizer-{DateTime.Now:yyyyMMdd-HHmm}.db",
            InitialDirectory = _service.BackupDirectory
        };
        if (dialog.ShowDialog() != true) return;

        IsBusy = true;
        Status = "Datenbank wird gesichert …";
        try
        {
            string path = await Task.Run(() => _service.CreateDatabaseBackup(dialog.FileName)).ConfigureAwait(true);
            Status = "Sicherung erstellt: " + path;
        }
        catch (Exception ex) { Status = "Sicherung fehlgeschlagen: " + ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task RestoreBackupAsync()
    {
        if (IsBusy) return;
        var dialog = new OpenFileDialog
        {
            Title = "NexusOptimizer-Sicherung auswählen",
            Filter = "NexusOptimizer-Datenbank (*.db)|*.db",
            CheckFileExists = true,
            Multiselect = false,
            InitialDirectory = _service.BackupDirectory
        };
        if (dialog.ShowDialog() != true) return;

        var answer = MessageBox.Show(
            "Die ausgewählte Datei ersetzt die aktuellen Sitzungsdaten und Einstellungen. Vorher wird eine Sicherheitskopie der aktuellen Datenbank erstellt. Fortfahren?",
            "Datenbank wiederherstellen", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        IsBusy = true;
        Status = "Sicherung wird geprüft und wiederhergestellt …";
        try
        {
            string safetyCopy = await Task.Run(() => _service.RestoreDatabaseBackup(dialog.FileName)).ConfigureAwait(true);
            Status = "Datenbank wiederhergestellt. Sicherheitskopie: " + safetyCopy;
        }
        catch (Exception ex) { Status = "Wiederherstellung fehlgeschlagen: " + ex.Message; }
        finally { IsBusy = false; }
    }
}
