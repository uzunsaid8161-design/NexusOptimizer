using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace NexusOptimizer.ViewModels;

public partial class MainViewModel : ObservableObject
{
    [ObservableProperty] private int _selectedPage;
    [ObservableProperty] private string _pageTitle = "Dashboard";
    [ObservableProperty] private string _pageSubtitle = "Live-FPS, Frametimes und Systemauslastung";
    [ObservableProperty] private bool _isElevated;

    public bool IsRestricted => !IsElevated;

    public MainViewModel()
    {
        IsElevated = CheckElevation();
    }

    private static bool CheckElevation()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    [RelayCommand]
    private void Navigate(string index)
    {
        if (!int.TryParse(index, out int target)) return;

        (PageTitle, PageSubtitle) = target switch
        {
            0 => ("Dashboard", "Live-FPS, Frametimes und Systemauslastung"),
            1 => ("Statistik", "Live-FPS, Frametimes und Systemlast der aktuellen App-Sitzung"),
            2 => ("Optimizer", "Tweaks mit Rollback und Protokoll"),
            3 => ("Overlay", "FPS und Systemwerte als click-through HUD anzeigen"),
            4 => ("Einstellungen", "Bewegung, Windows-Autostart und lokale Daten verwalten"),
            5 => ("Autostart", "Run/RunOnce und Startordner durchsuchen, filtern und gesichert verwalten"),
            6 => ("Backup", "Sitzungsdaten lokal sichern und wiederherstellen"),
            7 => ("Cleaner", "Alte temporäre Dateien sicher bereinigen"),
            8 => ("Display Control", "Monitore, Helligkeit und tatsächlich verfügbare Anzeigemodi verwalten"),
            9 => ("Prozessmanager", "Live-Ressourcen, Prozessdetails, Gaming-Beobachtung und lokaler Verlauf"),
            _ => (PageTitle, PageSubtitle)
        };

        SelectedPage = target;
    }
}
