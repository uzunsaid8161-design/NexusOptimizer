namespace NexusOptimizer.Models;

public sealed record DisplayMode(int Width, int Height, int RefreshRate, int BitsPerPixel)
{
    public string Label => $"{Width} × {Height} · {RefreshRate} Hz · {BitsPerPixel}-Bit";
    public override string ToString() => Label;
}

public sealed record DisplayDevice(
    string DeviceName,
    string MonitorName,
    string Manufacturer,
    bool IsPrimary,
    int X,
    int Y,
    int Width,
    int Height,
    int RefreshRate,
    int BitsPerPixel,
    int? ScalingPercent,
    string? ColorProfile,
    string HdrStatus,
    IReadOnlyList<DisplayMode> Modes,
    string BrightnessStatus,
    int? BrightnessPercent,
    string? BrightnessProvider,
    string? BrightnessInstance)
{
    public string CurrentMode => $"{Width} × {Height} bei {RefreshRate} Hz";
    public string Position => $"X {X}, Y {Y} · {Width} × {Height}";
    public string Scaling => ScalingPercent is int value ? $"{value}%" : "Nicht gemeldet";
    public string ManufacturerText => string.IsNullOrWhiteSpace(Manufacturer) ? "Nicht gemeldet" : Manufacturer;
    public string ColorProfileText => string.IsNullOrWhiteSpace(ColorProfile) ? "Nicht gemeldet" : ColorProfile;
    public DisplayMode ActiveMode => new(Width, Height, RefreshRate, BitsPerPixel);
    public bool CanSetBrightness => BrightnessPercent.HasValue && BrightnessProvider is not null;
    public string BrightnessText => BrightnessPercent is int value ? $"{value}%" : "Nicht unterstützt";
    public override string ToString() => IsPrimary ? $"{MonitorName} · Hauptdisplay" : MonitorName;
}

public sealed record DisplayProfile(
    string Name,
    string DeviceName,
    int Width,
    int Height,
    int RefreshRate,
    int BitsPerPixel,
    int? BrightnessPercent,
    DateTime SavedAt)
{
    public override string ToString() => $"{Name} · {Width} × {Height} · {RefreshRate} Hz";
}
