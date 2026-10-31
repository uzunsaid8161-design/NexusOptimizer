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
            1 => ("Statistik", "Sessions und Bestwerte – in Arbeit"),
            2 => ("Optimizer", "Tweaks mit Rollback und Protokoll"),
            3 => ("Overlay", "In-Game-Overlay – in Arbeit"),
            4 => ("Einstellungen", "Konfiguration – in Arbeit"),
            _ => (PageTitle, PageSubtitle)
        };

        SelectedPage = target;
    }
}
