using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using NexusOptimizer.Models;
using NexusOptimizer.Services;

namespace NexusOptimizer.ViewModels;

public partial class DisplayControlViewModel : ObservableObject
{
    private static readonly TimeSpan ConfirmationTime = TimeSpan.FromSeconds(15);
    private readonly DisplayControlService _service;
    private CancellationTokenSource? _rollbackCancellation;
    private string? _pendingDeviceName;
    private DisplayMode? _pendingPreviousMode;
    private int? _brightnessAfterConfirm;
    private string? _brightnessProviderAfterConfirm;
    private string? _brightnessInstanceAfterConfirm;

    public ObservableCollection<DisplayDevice> Displays { get; } = [];
    public ObservableCollection<DisplayMode> Modes { get; } = [];
    public ObservableCollection<DisplayProfile> Profiles { get; } = [];

    [ObservableProperty] private DisplayDevice? _selectedDisplay;
    [ObservableProperty] private DisplayMode? _selectedMode;
    [ObservableProperty] private DisplayProfile? _selectedProfile;
    [ObservableProperty] private string _profileName = string.Empty;
    [ObservableProperty] private int _brightnessValue = 50;
    [ObservableProperty] private string _status = "Displayinformationen werden geladen.";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _hasPendingChange;
    [ObservableProperty] private int _remainingSeconds;
    private bool _hasInitialized;

    public int MonitorCount => Displays.Count;
    public string HdrStatus => SelectedDisplay?.HdrStatus ?? "Kein Display ausgewählt.";
    public string AdaptiveSyncStatus => "Vom Grafiktreiber nicht verlässlich gemeldet.";
    public bool CanEditDisplay => !HasPendingChange && !IsBusy;
    public string BrightnessStatus => SelectedDisplay?.BrightnessStatus ?? "Kein Display ausgewählt.";
    public string BrightnessText => SelectedDisplay?.BrightnessText ?? "—";
    public string PendingText => HasPendingChange
        ? $"Neue Anzeigeeinstellung testen: noch {RemainingSeconds} Sekunden. Ohne Bestätigung wird zurückgesetzt."
        : string.Empty;
    public bool CanAdjustBrightness => !IsBusy && !HasPendingChange && SelectedDisplay?.CanSetBrightness == true;
    public string HighestRefreshText => SelectedDisplay is null || SelectedDisplay.Modes.Count == 0 ? "Nicht gemeldet" :
        $"{SelectedDisplay.Modes.Max(x => x.RefreshRate)} Hz · höchster vom Treiber gemeldeter Modus";

    public DisplayControlViewModel(DisplayControlService service)
    {
        _service = service;
    }

    public void EnsureLoaded()
    {
        if (_hasInitialized) return;
        _hasInitialized = true;
        _ = RefreshDisplaysAsync();
    }

    partial void OnSelectedDisplayChanged(DisplayDevice? value)
    {
        Modes.Clear();
        if (value is not null)
        {
            foreach (var mode in value.Modes) Modes.Add(mode);
            SelectedMode = Modes.FirstOrDefault(x => x.Width == value.Width && x.Height == value.Height
                && x.RefreshRate == value.RefreshRate && x.BitsPerPixel == value.BitsPerPixel)
                ?? Modes.FirstOrDefault();
            BrightnessValue = value.BrightnessPercent ?? 50;
        }
        LoadProfiles();
        OnPropertyChanged(nameof(BrightnessStatus));
        OnPropertyChanged(nameof(BrightnessText));
        OnPropertyChanged(nameof(HdrStatus));
        OnPropertyChanged(nameof(CanAdjustBrightness));
        OnPropertyChanged(nameof(HighestRefreshText));
        OnPropertyChanged(nameof(MonitorCount));
    }

    partial void OnHasPendingChangeChanged(bool value)
    {
        OnPropertyChanged(nameof(PendingText));
        OnPropertyChanged(nameof(CanEditDisplay));
        OnPropertyChanged(nameof(CanAdjustBrightness));
    }
    partial void OnRemainingSecondsChanged(int value) => OnPropertyChanged(nameof(PendingText));
    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanEditDisplay));
        OnPropertyChanged(nameof(CanAdjustBrightness));
    }

    [RelayCommand]
    private async Task RefreshDisplaysAsync()
    {
        if (IsBusy || HasPendingChange) return;
        IsBusy = true;
        try
        {
            string? oldDevice = SelectedDisplay?.DeviceName;
            var displays = await Task.Run(_service.GetDisplays);
            Displays.Clear();
            foreach (var display in displays) Displays.Add(display);
            SelectedDisplay = Displays.FirstOrDefault(x => string.Equals(x.DeviceName, oldDevice, StringComparison.OrdinalIgnoreCase))
                ?? Displays.FirstOrDefault(x => x.IsPrimary) ?? Displays.FirstOrDefault();
            Status = Displays.Count == 0 ? "Windows meldet derzeit kein aktives Display." : $"{Displays.Count} aktives Display(s) erkannt. Es werden nur vom Treiber gemeldete Modi angezeigt.";
            OnPropertyChanged(nameof(MonitorCount));
        }
        catch (Exception ex)
        {
            Status = "Displayinformationen konnten nicht gelesen werden: " + ex.Message;
            Serilog.Log.Warning(ex, "Displayinformationen konnten nicht gelesen werden");
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task ApplyModeAsync()
    {
        if (SelectedDisplay is null || SelectedMode is null || HasPendingChange || IsBusy) return;
        if (SelectedMode == SelectedDisplay.ActiveMode)
        {
            Status = "Dieser Modus ist bereits aktiv.";
            return;
        }

        var display = SelectedDisplay;
        var requestedMode = SelectedMode;
        var oldMode = display.ActiveMode;
        try
        {
            IsBusy = true;
            await Task.Run(() => _service.ApplyMode(display.DeviceName, requestedMode));
            int displayIndex = Displays.IndexOf(display);
            if (displayIndex >= 0)
            {
                var updated = display with
                {
                    Width = requestedMode.Width,
                    Height = requestedMode.Height,
                    RefreshRate = requestedMode.RefreshRate,
                    BitsPerPixel = requestedMode.BitsPerPixel
                };
                Displays[displayIndex] = updated;
                SelectedDisplay = updated;
                SelectedMode = Modes.FirstOrDefault(x => x == requestedMode) ?? requestedMode;
            }
            _pendingDeviceName = display.DeviceName;
            _pendingPreviousMode = oldMode;
            HasPendingChange = true;
            RemainingSeconds = (int)ConfirmationTime.TotalSeconds;
            Status = $"{requestedMode.Label} wird vorübergehend getestet. Bestätige die Darstellung innerhalb von 15 Sekunden.";
            _ = RunRollbackCountdownAsync();
        }
        catch (Exception ex)
        {
            _brightnessAfterConfirm = null;
            _brightnessProviderAfterConfirm = null;
            _brightnessInstanceAfterConfirm = null;
            Status = "Anzeigemodus wurde nicht geändert: " + ex.Message;
            Serilog.Log.Warning(ex, "Anzeigemodus konnte nicht geändert werden");
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void SelectHighestRefresh()
    {
        if (SelectedDisplay is null || Modes.Count == 0) return;
        int highest = Modes.Max(x => x.RefreshRate);
        SelectedMode = Modes.Where(x => x.RefreshRate == highest)
            .OrderByDescending(x => x.Width * (long)x.Height).First();
        Status = $"Höchste gemeldete Bildrate ausgewählt: {SelectedMode.Label}. Mit „Modus sicher testen“ anwenden.";
    }

    [RelayCommand]
    private async Task ApplyPresetAsync(string preset)
    {
        if (SelectedDisplay is null || IsBusy || HasPendingChange || Modes.Count == 0) return;
        var current = SelectedDisplay.ActiveMode;
        DisplayMode target = current;
        int? brightness = null;
        switch (preset)
        {
            case "competitive":
                target = Modes.Where(x => x.Width == current.Width && x.Height == current.Height)
                    .OrderByDescending(x => x.RefreshRate).FirstOrDefault()
                    ?? Modes.OrderByDescending(x => x.RefreshRate).ThenByDescending(x => x.Width * (long)x.Height).First();
                break;
            case "movie":
                target = Modes.Where(x => x.Width == current.Width && x.Height == current.Height)
                    .OrderBy(x => Math.Abs(x.RefreshRate - 60)).FirstOrDefault() ?? current;
                break;
            case "office":
                brightness = SelectedDisplay.CanSetBrightness ? 50 : null;
                break;
            case "night":
                if (!SelectedDisplay.CanSetBrightness)
                {
                    Status = "Der Monitor meldet keine softwareseitige Helligkeitssteuerung. Nutze den Nachtmodus über Windows.";
                    return;
                }
                brightness = 35;
                break;
            default:
                return;
        }

        string label = preset switch
        {
            "competitive" => "Competitive Gaming",
            "movie" => "Film",
            "office" => "Büro",
            _ => "Nacht"
        };
        if (target != current)
        {
            SelectedMode = Modes.FirstOrDefault(x => x.Width == target.Width && x.Height == target.Height
                && x.RefreshRate == target.RefreshRate && x.BitsPerPixel == target.BitsPerPixel);
            _brightnessAfterConfirm = brightness;
            _brightnessProviderAfterConfirm = SelectedDisplay.BrightnessProvider;
            _brightnessInstanceAfterConfirm = SelectedDisplay.BrightnessInstance;
            await ApplyModeAsync();
            if (HasPendingChange) Status = $"Profil „{label}“ wird getestet. Bestätige die Anzeige, damit es übernommen wird.";
            return;
        }
        if (brightness is int level)
        {
            BrightnessValue = level;
            await SetBrightnessAsync();
            Status = $"Profil „{label}“: " + Status;
            return;
        }
        Status = $"Profil „{label}“ ist für den aktuellen Modus bereits aktiv.";
    }

    [RelayCommand]
    private async Task KeepModeAsync()
    {
        if (!HasPendingChange) return;
        _rollbackCancellation?.Cancel();
        HasPendingChange = false;
        _pendingPreviousMode = null;
        _pendingDeviceName = null;
        Status = "Anzeigemodus beibehalten.";
        int? brightness = _brightnessAfterConfirm;
        _brightnessAfterConfirm = null;
        if (brightness is int value && SelectedDisplay is { } display)
        {
            try
            {
                Status = await Task.Run(() => _service.SetBrightness(display.DeviceName,
                    _brightnessProviderAfterConfirm, _brightnessInstanceAfterConfirm, value));
            }
            catch (Exception ex) { Status = "Profilmodus beibehalten, Helligkeit aber nicht geändert: " + ex.Message; }
        }
        _brightnessProviderAfterConfirm = null;
        _brightnessInstanceAfterConfirm = null;
        await RefreshDisplaysAsync();
    }

    [RelayCommand]
    private async Task RollbackModeAsync()
    {
        if (!HasPendingChange || _pendingDeviceName is null || _pendingPreviousMode is null) return;
        _rollbackCancellation?.Cancel();
        string device = _pendingDeviceName;
        DisplayMode mode = _pendingPreviousMode;
        HasPendingChange = false;
        _pendingDeviceName = null;
        _pendingPreviousMode = null;
        _brightnessAfterConfirm = null;
        _brightnessProviderAfterConfirm = null;
        _brightnessInstanceAfterConfirm = null;
        try
        {
            await Task.Run(() => _service.RestoreMode(device, mode));
            Status = "Vorherige Auflösung und Bildwiederholrate wiederhergestellt.";
        }
        catch (Exception ex)
        {
            Status = "Automatische Rücknahme fehlgeschlagen. Öffne Windows-Anzeigeeinstellungen: " + ex.Message;
            Serilog.Log.Error(ex, "Displaymodus-Rollback fehlgeschlagen");
        }
        await RefreshDisplaysAsync();
    }

    [RelayCommand]
    private async Task SetBrightnessAsync()
    {
        if (SelectedDisplay is not { CanSetBrightness: true } display || HasPendingChange || IsBusy) return;
        try
        {
            IsBusy = true;
            Status = await Task.Run(() => _service.SetBrightness(display.DeviceName,
                display.BrightnessProvider, display.BrightnessInstance, BrightnessValue));
            await RefreshDisplaysCoreAsync();
        }
        catch (Exception ex)
        {
            Status = "Helligkeit wurde nicht geändert: " + ex.Message;
            Serilog.Log.Warning(ex, "Monitorhelligkeit konnte nicht geändert werden");
        }
        finally { IsBusy = false; }
    }

    public Task ApplyBrightnessFromSliderAsync() => SetBrightnessAsync();

    [RelayCommand]
    private async Task DecreaseBrightnessAsync()
    {
        BrightnessValue = Math.Max(0, BrightnessValue - 10);
        await SetBrightnessAsync();
    }

    [RelayCommand]
    private async Task IncreaseBrightnessAsync()
    {
        BrightnessValue = Math.Min(100, BrightnessValue + 10);
        await SetBrightnessAsync();
    }

    [RelayCommand]
    private async Task ResetBrightnessAsync()
    {
        BrightnessValue = 50;
        await SetBrightnessAsync();
    }

    [RelayCommand]
    private void SaveProfile()
    {
        if (SelectedDisplay is null || string.IsNullOrWhiteSpace(ProfileName))
        {
            Status = "Wähle ein Display und gib einen Profilnamen ein.";
            return;
        }
        try
        {
            _service.SaveProfile(new DisplayProfile(ProfileName.Trim(), SelectedDisplay.DeviceName,
                SelectedDisplay.Width, SelectedDisplay.Height, SelectedDisplay.RefreshRate, SelectedDisplay.BitsPerPixel,
                SelectedDisplay.BrightnessPercent, DateTime.Now));
            LoadProfiles();
            Status = $"Displayprofil „{ProfileName.Trim()}“ gespeichert.";
        }
        catch (Exception ex) { Status = "Profil konnte nicht gespeichert werden: " + ex.Message; }
    }

    [RelayCommand]
    private async Task ApplyProfileAsync()
    {
        if (SelectedDisplay is null || SelectedProfile is null || HasPendingChange) return;
        if (!string.Equals(SelectedProfile.DeviceName, SelectedDisplay.DeviceName, StringComparison.OrdinalIgnoreCase))
        {
            Status = "Dieses Profil wurde für ein anderes Display gespeichert. Wähle zuerst dieses Display.";
            return;
        }
        var mode = Modes.FirstOrDefault(x => x.Width == SelectedProfile.Width && x.Height == SelectedProfile.Height
            && x.RefreshRate == SelectedProfile.RefreshRate && x.BitsPerPixel == SelectedProfile.BitsPerPixel);
        if (mode is null)
        {
            Status = "Das Profil enthält einen Modus, den Windows aktuell nicht anbietet.";
            return;
        }
        SelectedMode = mode;
        if (mode == SelectedDisplay.ActiveMode)
        {
            if (SelectedProfile.BrightnessPercent is int currentBrightness && SelectedDisplay.CanSetBrightness)
            {
                try { Status = await Task.Run(() => _service.SetBrightness(SelectedDisplay.DeviceName, SelectedDisplay.BrightnessProvider, SelectedDisplay.BrightnessInstance, currentBrightness)); }
                catch (Exception ex) { Status = "Profilmodus ist bereits aktiv; Helligkeit konnte nicht gesetzt werden: " + ex.Message; }
                await RefreshDisplaysAsync();
            }
            else Status = "Der Profilmodus ist bereits aktiv; für die gespeicherte Helligkeit gibt es keine erkannte Steuerung.";
            return;
        }
        _brightnessAfterConfirm = SelectedProfile.BrightnessPercent;
        _brightnessProviderAfterConfirm = SelectedDisplay.BrightnessProvider;
        _brightnessInstanceAfterConfirm = SelectedDisplay.BrightnessInstance;
        await ApplyModeAsync();
    }

    [RelayCommand]
    private void DeleteProfile()
    {
        if (SelectedProfile is null) return;
        try
        {
            _service.DeleteProfile(SelectedProfile);
            Status = $"Displayprofil „{SelectedProfile.Name}“ gelöscht.";
            LoadProfiles();
        }
        catch (Exception ex) { Status = "Profil konnte nicht gelöscht werden: " + ex.Message; }
    }

    [RelayCommand]
    private void ExportProfiles()
    {
        var dialog = new SaveFileDialog { Filter = "Nexus Displayprofile (*.json)|*.json", DefaultExt = ".json", AddExtension = true, FileName = "Nexus-Displayprofile.json" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            _service.ExportProfiles(dialog.FileName);
            Status = "Displayprofile exportiert.";
        }
        catch (Exception ex) { Status = "Displayprofile konnten nicht exportiert werden: " + ex.Message; }
    }

    [RelayCommand]
    private void ImportProfiles()
    {
        var dialog = new OpenFileDialog { Filter = "Nexus Displayprofile (*.json)|*.json", CheckFileExists = true, Multiselect = false };
        if (dialog.ShowDialog() != true) return;
        try
        {
            int count = _service.ImportProfiles(dialog.FileName);
            LoadProfiles();
            Status = $"{count} Displayprofil(e) importiert. Modi werden vor einer Anwendung erneut gegen Windows geprüft.";
        }
        catch (Exception ex) { Status = "Displayprofile konnten nicht importiert werden: " + ex.Message; }
    }

    [RelayCommand]
    private void OpenDisplaySettings(string page)
    {
        string uri = page switch
        {
            "nightlight" => "ms-settings:nightlight",
            "advanced" => "ms-settings:display-advanced",
            "color" => "ms-settings:display-color",
            _ => "ms-settings:display"
        };
        try
        {
            if (page == "color") Process.Start(new ProcessStartInfo("colorcpl.exe") { UseShellExecute = true });
            else Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch (Exception ex) { Status = "Windows-Anzeigeeinstellungen konnten nicht geöffnet werden: " + ex.Message; }
    }

    public void RollbackPendingOnClose()
    {
        if (!HasPendingChange || _pendingDeviceName is null || _pendingPreviousMode is null) return;
        _rollbackCancellation?.Cancel();
        try { _service.RestoreMode(_pendingDeviceName, _pendingPreviousMode); }
        catch (Exception ex) { Serilog.Log.Error(ex, "Displaymodus konnte beim Schließen nicht zurückgesetzt werden"); }
        HasPendingChange = false;
        _pendingDeviceName = null;
        _pendingPreviousMode = null;
    }

    private async Task RunRollbackCountdownAsync()
    {
        _rollbackCancellation?.Cancel();
        _rollbackCancellation = new CancellationTokenSource();
        var token = _rollbackCancellation.Token;
        try
        {
            while (RemainingSeconds > 0)
            {
                await Task.Delay(1000, token);
                if (token.IsCancellationRequested || !HasPendingChange) return;
                RemainingSeconds--;
            }
            await RollbackModeAsync();
        }
        catch (OperationCanceledException) { }
    }

    private async Task RefreshDisplaysCoreAsync()
    {
        string? current = SelectedDisplay?.DeviceName;
        var displays = await Task.Run(_service.GetDisplays);
        Displays.Clear();
        foreach (var item in displays) Displays.Add(item);
        SelectedDisplay = Displays.FirstOrDefault(x => string.Equals(x.DeviceName, current, StringComparison.OrdinalIgnoreCase))
            ?? Displays.FirstOrDefault(x => x.IsPrimary) ?? Displays.FirstOrDefault();
        OnPropertyChanged(nameof(MonitorCount));
    }

    private void LoadProfiles()
    {
        Profiles.Clear();
        if (SelectedDisplay is null) return;
        foreach (var profile in _service.ReadProfiles().Where(x => string.Equals(x.DeviceName, SelectedDisplay.DeviceName, StringComparison.OrdinalIgnoreCase)))
            Profiles.Add(profile);
        SelectedProfile = Profiles.FirstOrDefault();
    }
}
