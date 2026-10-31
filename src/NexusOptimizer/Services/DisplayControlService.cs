using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using NexusOptimizer.Models;

namespace NexusOptimizer.Services;

/// <summary>Reads actual Windows display modes and applies only driver-advertised modes.</summary>
public sealed class DisplayControlService
{
    private const int EnumCurrentSettings = -1;
    private const int CdsTest = 0x00000002;
    private const uint DmBitsPerPel = 0x00040000;
    private const uint DmPelsWidth = 0x00080000;
    private const uint DmPelsHeight = 0x00100000;
    private const uint DmDisplayFrequency = 0x00400000;
    private const uint DisplayDeviceActive = 0x00000001;
    private const uint DisplayDevicePrimary = 0x00000004;
    private const uint MonitorDefaultToNearest = 2;
    private static readonly string ProfilePath = Path.Combine(AppPaths.Root, "display-profiles.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly object GammaLock = new();
    private static readonly Dictionary<string, ushort[]> OriginalGammaRamps = new(StringComparer.OrdinalIgnoreCase);

    private sealed record WmiMonitor(string InstanceName, string Manufacturer, string FriendlyName, int? Brightness, bool CanSetBrightness);
    private sealed record BrightnessRead(int Percent, string Provider);

    public IReadOnlyList<DisplayDevice> GetDisplays()
    {
        var wmi = ReadWmiMonitors();
        var result = new List<DisplayDevice>();
        for (uint index = 0; ; index++)
        {
            var adapter = NewDisplayDevice();
            if (!EnumDisplayDevices(null, index, ref adapter, 0)) break;
            if ((adapter.StateFlags & DisplayDeviceActive) == 0 || string.IsNullOrWhiteSpace(adapter.DeviceName)) continue;

            var monitor = NewDisplayDevice();
            EnumDisplayDevices(adapter.DeviceName, 0, ref monitor, 0);
            var current = NewMode();
            current.dmSize = (ushort)Marshal.SizeOf<DEVMODE>();
            if (!EnumDisplaySettings(adapter.DeviceName, EnumCurrentSettings, ref current)) continue;

            string deviceId = monitor.DeviceID ?? string.Empty;
            WmiMonitor? match = FindWmiMonitor(wmi, deviceId);
            string monitorName = !string.IsNullOrWhiteSpace(match?.FriendlyName)
                ? match.FriendlyName
                : !string.IsNullOrWhiteSpace(monitor.DeviceString) ? monitor.DeviceString : adapter.DeviceString;
            if (string.IsNullOrWhiteSpace(monitorName)) monitorName = adapter.DeviceName;

            var brightness = ReadBrightness(adapter.DeviceName, match);
            int width = checked((int)current.dmPelsWidth);
            int height = checked((int)current.dmPelsHeight);
            var modes = ReadModes(adapter.DeviceName);
            var position = current.dmPosition;
            result.Add(new DisplayDevice(
                adapter.DeviceName,
                monitorName.Trim(),
                match?.Manufacturer ?? ManufacturerFromPnpId(deviceId),
                (adapter.StateFlags & DisplayDevicePrimary) != 0,
                position.X,
                position.Y,
                width,
                height,
                checked((int)current.dmDisplayFrequency),
                checked((int)current.dmBitsPerPel),
                ReadScaling(position.X, position.Y),
                ReadColorProfile(adapter.DeviceName),
                ReadHdrStatus(adapter.DeviceName),
                modes,
                brightness?.Provider ?? "Dieser Monitor meldet keine softwareseitige Helligkeitssteuerung.",
                brightness?.Percent,
                brightness?.Provider == "DDC/CI" ? "ddc" : brightness?.Provider == "Windows-WMI" ? "wmi" : null,
                match?.CanSetBrightness == true ? match.InstanceName : null));
        }
        return result;
    }

    public void ApplyMode(string deviceName, DisplayMode requested)
    {
        var advertised = ReadModes(deviceName);
        if (!advertised.Contains(requested))
            throw new InvalidOperationException("Dieser Anzeigemodus wird vom Grafiktreiber aktuell nicht angeboten.");
        var previous = GetCurrentMode(deviceName);
        try { SetMode(deviceName, requested); }
        catch
        {
            try { SetMode(deviceName, previous); }
            catch (Exception rollbackError) { Serilog.Log.Error(rollbackError, "Displaymodus konnte nach einem fehlgeschlagenen Wechsel nicht sofort zurückgesetzt werden"); }
            throw;
        }
    }

    public DisplayMode GetCurrentMode(string deviceName)
    {
        var mode = NewMode();
        mode.dmSize = (ushort)Marshal.SizeOf<DEVMODE>();
        if (!EnumDisplaySettings(deviceName, EnumCurrentSettings, ref mode))
            throw new InvalidOperationException("Der aktuelle Anzeigemodus konnte nicht gelesen werden.");
        return ToDisplayMode(mode);
    }

    public void RestoreMode(string deviceName, DisplayMode previous) => SetMode(deviceName, previous);

    public string SetBrightness(string deviceName, string? provider, string? instanceName, int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        string? hardwareStatus = null;
        if (provider == "ddc")
        {
            if (TrySetDdcBrightness(deviceName, percent)) hardwareStatus = $"Monitor meldet {percent}% (DDC/CI)";
        }
        else if (provider == "wmi" && !string.IsNullOrWhiteSpace(instanceName))
        {
            using var searcher = new ManagementObjectSearcher("root\\WMI", "SELECT * FROM WmiMonitorBrightnessMethods");
            foreach (ManagementObject item in searcher.Get())
            {
                using (item)
                {
                    if (!string.Equals(item["InstanceName"]?.ToString(), instanceName, StringComparison.OrdinalIgnoreCase)) continue;
                    using var input = item.GetMethodParameters("WmiSetBrightness");
                    input["Timeout"] = (uint)1;
                    input["Brightness"] = (byte)percent;
                    using var output = item.InvokeMethod("WmiSetBrightness", input, null);
                    uint code = Convert.ToUInt32(output?["ReturnValue"] ?? 1);
                    if (code == 0) hardwareStatus = $"Monitor meldet {percent}% (Windows-WMI)";
                    else hardwareStatus = $"Monitorsteuerung meldet WMI-Code {code}";
                    break;
                }
            }
        }

        if (TrySetSoftwareBrightness(deviceName, percent))
            return $"Bildschirm sichtbar auf {percent}% gedimmt (ohne Overlay). {hardwareStatus ?? "Hardwarehelligkeit nicht verfügbar; Softwaredimmung aktiv."}";
        if (hardwareStatus is not null)
            return hardwareStatus + ". Der Grafiktreiber hat Softwaredimmung abgelehnt.";
        throw new InvalidOperationException("Monitor und Grafiktreiber haben die Helligkeitsänderung nicht übernommen.");
    }

    /// <summary>Restores each display's original color ramp when the app exits.</summary>
    public static void RestoreSoftwareBrightness()
    {
        lock (GammaLock)
        {
            foreach (var (device, ramp) in OriginalGammaRamps.ToArray())
            {
                IntPtr dc = CreateDC("DISPLAY", device, null, IntPtr.Zero);
                if (dc == IntPtr.Zero) continue;
                try { SetDeviceGammaRamp(dc, ramp); }
                catch { }
                finally { DeleteDC(dc); }
            }
            OriginalGammaRamps.Clear();
        }
    }

    private static bool TrySetSoftwareBrightness(string deviceName, int percent)
    {
        IntPtr dc = CreateDC("DISPLAY", deviceName, null, IntPtr.Zero);
        if (dc == IntPtr.Zero) return false;
        try
        {
            lock (GammaLock)
            {
                if (!OriginalGammaRamps.TryGetValue(deviceName, out var original))
                {
                    original = new ushort[768];
                    if (!GetDeviceGammaRamp(dc, original)) return false;
                    OriginalGammaRamps[deviceName] = original;
                }

                if (percent >= 100) return SetDeviceGammaRamp(dc, original);
                var dimmed = new ushort[original.Length];
                double scale = percent / 100d;
                for (int i = 0; i < dimmed.Length; i++)
                    dimmed[i] = (ushort)Math.Clamp(Math.Round(original[i] * scale), 0, ushort.MaxValue);
                return SetDeviceGammaRamp(dc, dimmed);
            }
        }
        finally { DeleteDC(dc); }
    }

    public IReadOnlyList<DisplayProfile> ReadProfiles()
    {
        try
        {
            if (!File.Exists(ProfilePath)) return [];
            return JsonSerializer.Deserialize<List<DisplayProfile>>(File.ReadAllText(ProfilePath), JsonOptions) ?? [];
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Displayprofile konnten nicht gelesen werden");
            return [];
        }
    }

    public void SaveProfile(DisplayProfile profile)
    {
        var profiles = ReadProfiles().Where(x => !string.Equals(x.Name, profile.Name, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(x.DeviceName, profile.DeviceName, StringComparison.OrdinalIgnoreCase)).ToList();
        profiles.Add(profile);
        WriteProfiles(profiles);
    }

    public void DeleteProfile(DisplayProfile profile)
    {
        var profiles = ReadProfiles().Where(x => !string.Equals(x.Name, profile.Name, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(x.DeviceName, profile.DeviceName, StringComparison.OrdinalIgnoreCase)).ToList();
        WriteProfiles(profiles);
    }

    public void ExportProfiles(string filePath)
    {
        var profiles = ReadProfiles();
        if (profiles.Count == 0) throw new InvalidOperationException("Es sind noch keine Displayprofile gespeichert.");
        File.WriteAllText(filePath, JsonSerializer.Serialize(profiles, JsonOptions));
    }

    public int ImportProfiles(string filePath)
    {
        var imported = JsonSerializer.Deserialize<List<DisplayProfile>>(File.ReadAllText(filePath), JsonOptions)
            ?? throw new InvalidDataException("Die Profildatei enthält keine gültigen Daten.");
        if (imported.Count > 100) throw new InvalidDataException("Die Datei enthält zu viele Profile.");
        foreach (var profile in imported)
        {
            if (string.IsNullOrWhiteSpace(profile.Name) || profile.Name.Length > 64 || string.IsNullOrWhiteSpace(profile.DeviceName)
                || profile.Width is < 320 or > 16384 || profile.Height is < 200 or > 16384
                || profile.RefreshRate is < 1 or > 1000 || profile.BitsPerPixel is < 8 or > 128
                || profile.BrightnessPercent is < 0 or > 100)
                throw new InvalidDataException("Mindestens ein Displayprofil enthält ungültige Werte.");
        }

        var merged = ReadProfiles().ToList();
        foreach (var profile in imported)
        {
            merged.RemoveAll(x => string.Equals(x.Name, profile.Name, StringComparison.OrdinalIgnoreCase)
                && string.Equals(x.DeviceName, profile.DeviceName, StringComparison.OrdinalIgnoreCase));
            merged.Add(profile);
        }
        WriteProfiles(merged);
        return imported.Count;
    }

    private static void WriteProfiles(List<DisplayProfile> profiles)
    {
        Directory.CreateDirectory(AppPaths.Root);
        string temp = ProfilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(profiles, JsonOptions));
        File.Move(temp, ProfilePath, true);
    }

    private static void SetMode(string deviceName, DisplayMode requested)
    {
        var mode = NewMode();
        mode.dmSize = (ushort)Marshal.SizeOf<DEVMODE>();
        if (!EnumDisplaySettings(deviceName, EnumCurrentSettings, ref mode))
            throw new InvalidOperationException("Der bestehende Anzeigemodus konnte nicht gesichert werden.");
        mode.dmPelsWidth = (uint)requested.Width;
        mode.dmPelsHeight = (uint)requested.Height;
        mode.dmDisplayFrequency = (uint)requested.RefreshRate;
        mode.dmBitsPerPel = (uint)requested.BitsPerPixel;
        mode.dmFields = DmPelsWidth | DmPelsHeight | DmDisplayFrequency | DmBitsPerPel;
        int test = ChangeDisplaySettingsEx(deviceName, ref mode, IntPtr.Zero, CdsTest, IntPtr.Zero);
        if (test != 0) throw new InvalidOperationException(ModeError(test));
        int apply = ChangeDisplaySettingsEx(deviceName, ref mode, IntPtr.Zero, 0, IntPtr.Zero);
        if (apply != 0) throw new InvalidOperationException(ModeError(apply));
    }

    private static string ModeError(int result) => result switch
    {
        -1 => "Windows kann den Grafikmodus nicht ändern.",
        -2 => "Dieser Anzeigemodus wird vom Treiber nicht unterstützt.",
        -3 => "Der Treiber hat die Änderung abgelehnt.",
        -4 => "Die Grafikmodusparameter sind ungültig.",
        -5 => "Windows kann die Änderung nicht speichern.",
        -6 => "Ein Neustart wäre nötig; die Einstellung wurde nicht übernommen.",
        _ => $"Windows hat den Anzeigemodus abgelehnt (Code {result})."
    };

    private static IReadOnlyList<DisplayMode> ReadModes(string deviceName)
    {
        var modes = new HashSet<DisplayMode>();
        for (int index = 0; index < 4096; index++)
        {
            var mode = NewMode();
            mode.dmSize = (ushort)Marshal.SizeOf<DEVMODE>();
            if (!EnumDisplaySettings(deviceName, index, ref mode)) break;
            if (mode.dmPelsWidth == 0 || mode.dmPelsHeight == 0 || mode.dmDisplayFrequency == 0 || mode.dmBitsPerPel == 0) continue;
            modes.Add(ToDisplayMode(mode));
        }
        return modes.OrderByDescending(x => x.Width * (long)x.Height).ThenByDescending(x => x.RefreshRate).ToList();
    }

    private static DisplayMode ToDisplayMode(DEVMODE mode) => new(
        checked((int)mode.dmPelsWidth), checked((int)mode.dmPelsHeight),
        checked((int)mode.dmDisplayFrequency), checked((int)mode.dmBitsPerPel));

    private static BrightnessRead? ReadBrightness(string deviceName, WmiMonitor? wmi)
    {
        if (TryGetDdcBrightness(deviceName, out int ddcPercent)) return new(ddcPercent, "DDC/CI");
        if (wmi?.CanSetBrightness == true && wmi.Brightness is int level) return new(level, "Windows-WMI");
        return null;
    }

    private static bool TryGetDdcBrightness(string deviceName, out int percent)
    {
        int read = 0;
        bool success = WithPhysicalMonitor(deviceName, handle =>
        {
            if (!GetMonitorBrightness(handle, out uint minimum, out uint current, out uint maximum) || maximum <= minimum) return false;
            read = (int)Math.Round((current - minimum) * 100.0 / (maximum - minimum));
            return true;
        });
        percent = read;
        return success;
    }

    private static bool TrySetDdcBrightness(string deviceName, int percent)
    {
        return WithPhysicalMonitor(deviceName, handle =>
        {
            if (!GetMonitorBrightness(handle, out uint minimum, out _, out uint maximum) || maximum <= minimum) return false;
            uint value = minimum + (uint)Math.Round((maximum - minimum) * percent / 100.0);
            return SetMonitorBrightness(handle, value);
        });
    }

    private static bool WithPhysicalMonitor(string deviceName, Func<IntPtr, bool> action)
    {
        IntPtr target = IntPtr.Zero;
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            var info = NewMonitorInfo();
            if (GetMonitorInfo(monitor, ref info) && string.Equals(info.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
            {
                target = monitor;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        if (target == IntPtr.Zero || !GetNumberOfPhysicalMonitorsFromHMONITOR(target, out uint count) || count == 0) return false;
        var physical = new PHYSICAL_MONITOR[count];
        if (!GetPhysicalMonitorsFromHMONITOR(target, count, physical)) return false;
        bool result = false;
        try
        {
            foreach (var item in physical)
            {
                try { if (action(item.Handle)) { result = true; break; } }
                catch { }
            }
        }
        finally { DestroyPhysicalMonitors(count, physical); }
        return result;
    }

    private static List<WmiMonitor> ReadWmiMonitors()
    {
        var identities = new Dictionary<string, (string Manufacturer, string Name)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcher = new ManagementObjectSearcher("root\\WMI", "SELECT * FROM WmiMonitorID");
            foreach (ManagementObject item in searcher.Get())
            {
                using (item)
                {
                    string instance = item["InstanceName"]?.ToString() ?? string.Empty;
                    identities[NormalizeMonitorId(instance)] = (
                        DecodeWmiChars(item["ManufacturerName"]), DecodeWmiChars(item["UserFriendlyName"]));
                }
            }
        }
        catch (Exception ex) { Serilog.Log.Debug(ex, "Monitoridentitäten aus WMI nicht verfügbar"); }

        var brightness = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcher = new ManagementObjectSearcher("root\\WMI", "SELECT * FROM WmiMonitorBrightness");
            foreach (ManagementObject item in searcher.Get())
            {
                using (item)
                    if (item["InstanceName"] is object id && item["CurrentBrightness"] is object level)
                        brightness[NormalizeMonitorId(id.ToString()!)] = Convert.ToInt32(level);
            }
        }
        catch (Exception ex) { Serilog.Log.Debug(ex, "Monitorhelligkeit aus WMI nicht verfügbar"); }

        var methods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcher = new ManagementObjectSearcher("root\\WMI", "SELECT * FROM WmiMonitorBrightnessMethods");
            foreach (ManagementObject item in searcher.Get())
            {
                using (item)
                    if (item["InstanceName"] is object id) methods.Add(NormalizeMonitorId(id.ToString()!));
            }
        }
        catch (Exception ex) { Serilog.Log.Debug(ex, "Monitorhelligkeitssteuerung aus WMI nicht verfügbar"); }

        return identities.Select(x => new WmiMonitor(x.Key, x.Value.Manufacturer, x.Value.Name,
                brightness.TryGetValue(x.Key, out int level) ? level : null, methods.Contains(x.Key)))
            .ToList();
    }

    private static WmiMonitor? FindWmiMonitor(IEnumerable<WmiMonitor> monitors, string pnpId)
    {
        string wanted = NormalizeMonitorId(pnpId);
        return monitors.FirstOrDefault(x => string.Equals(x.InstanceName, wanted, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeMonitorId(string value)
    {
        string normalized = value.Trim().Replace("MONITOR\\", "DISPLAY\\", StringComparison.OrdinalIgnoreCase)
            .Replace("\\?\\", string.Empty, StringComparison.OrdinalIgnoreCase).Replace('#', '\\').TrimEnd('\\');
        // WMI appends an instance suffix such as _0; the PnP monitor ID does not.
        int marker = normalized.LastIndexOf('_');
        int lastSeparator = normalized.LastIndexOf('\\');
        if (marker > lastSeparator && marker + 1 < normalized.Length && normalized.AsSpan(marker + 1).ToString().All(char.IsDigit))
            normalized = normalized[..marker];
        return normalized;
    }

    private static string DecodeWmiChars(object? value)
    {
        if (value is not ushort[] codes) return string.Empty;
        return new string(codes.Where(x => x != 0).Select(x => (char)x).ToArray()).Trim();
    }

    private static string ManufacturerFromPnpId(string id)
    {
        string[] parts = id.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return string.Empty;
        string productId = parts[1];
        return productId.Length >= 3 ? productId[..3] : productId;
    }

    private static int? ReadScaling(int x, int y)
    {
        try
        {
            IntPtr monitor = MonitorFromPoint(new POINT { X = x + 1, Y = y + 1 }, MonitorDefaultToNearest);
            if (monitor != IntPtr.Zero && GetDpiForMonitor(monitor, 0, out uint dpiX, out _) == 0 && dpiX > 0)
                return (int)Math.Round(dpiX * 100.0 / 96.0);
        }
        catch { }
        return null;
    }

    private static string? ReadColorProfile(string deviceName)
    {
        IntPtr dc = CreateDC("DISPLAY", deviceName, null, IntPtr.Zero);
        if (dc == IntPtr.Zero) return null;
        try
        {
            uint length = 1024;
            var path = new StringBuilder((int)length);
            return GetICMProfile(dc, ref length, path) && path.Length > 0 ? path.ToString() : null;
        }
        finally { DeleteDC(dc); }
    }

    private static string ReadHdrStatus(string deviceName)
    {
        try
        {
            if (!TryReadDisplayPaths(out var paths)) return "Status von Windows nicht verfügbar.";
            foreach (var path in paths)
            {
                var source = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
                {
                    Header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                    {
                        Type = 1,
                        Size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                        AdapterId = path.SourceInfo.AdapterId,
                        Id = path.SourceInfo.Id
                    },
                    DeviceName = string.Empty
                };
                if (DisplayConfigGetDeviceInfo(ref source) != 0 ||
                    !string.Equals(source.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase)) continue;

                var color = new DISPLAYCONFIG_ADVANCED_COLOR_INFO
                {
                    Header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                    {
                        Type = 9,
                        Size = (uint)Marshal.SizeOf<DISPLAYCONFIG_ADVANCED_COLOR_INFO>(),
                        AdapterId = path.TargetInfo.AdapterId,
                        Id = path.TargetInfo.Id
                    }
                };
                if (DisplayConfigGetDeviceInfo(ref color) != 0) return "Status von Windows nicht verfügbar.";
                bool supported = (color.Value & 1) != 0;
                bool enabled = (color.Value & 2) != 0;
                if (!supported) return "HDR wird für dieses Display nicht unterstützt.";
                return enabled ? "Unterstützt und aktiviert" : "Unterstützt, derzeit deaktiviert";
            }
        }
        catch (Exception ex) { Serilog.Log.Debug(ex, "HDR-Status konnte nicht gelesen werden"); }
        return "Status von Windows nicht verfügbar.";
    }

    private static bool TryReadDisplayPaths(out List<DISPLAYCONFIG_PATH_INFO> paths)
    {
        paths = [];
        const uint onlyActivePaths = 0x00000002;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (GetDisplayConfigBufferSizes(onlyActivePaths, out uint pathCount, out uint modeCount) != 0 || pathCount == 0)
                return false;
            int pathSize = Marshal.SizeOf<DISPLAYCONFIG_PATH_INFO>();
            int modeSize = 64; // Native DISPLAYCONFIG_MODE_INFO is 64 bytes on 64-bit Windows.
            IntPtr pathBuffer = Marshal.AllocHGlobal(checked((int)pathCount * pathSize));
            IntPtr modeBuffer = Marshal.AllocHGlobal(Math.Max(modeSize, checked((int)modeCount * modeSize)));
            try
            {
                uint pathsReturned = pathCount;
                uint modesReturned = modeCount;
                int result = QueryDisplayConfig(onlyActivePaths, ref pathsReturned, pathBuffer, ref modesReturned, modeBuffer, IntPtr.Zero);
                if (result == 122) continue; // ERROR_INSUFFICIENT_BUFFER: the display state changed during enumeration.
                if (result != 0) return false;
                paths = new List<DISPLAYCONFIG_PATH_INFO>(checked((int)pathsReturned));
                for (int i = 0; i < pathsReturned; i++)
                    paths.Add(Marshal.PtrToStructure<DISPLAYCONFIG_PATH_INFO>(IntPtr.Add(pathBuffer, i * pathSize)));
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(pathBuffer);
                Marshal.FreeHGlobal(modeBuffer);
            }
        }
        return false;
    }

    private static DISPLAY_DEVICE NewDisplayDevice() => new()
    {
        cb = Marshal.SizeOf<DISPLAY_DEVICE>(), DeviceName = string.Empty, DeviceString = string.Empty,
        DeviceID = string.Empty, DeviceKey = string.Empty
    };

    private static DEVMODE NewMode() => new() { dmDeviceName = string.Empty, dmFormName = string.Empty };

    private static MONITORINFOEX NewMonitorInfo() => new() { cbSize = Marshal.SizeOf<MONITORINFOEX>(), DeviceName = string.Empty };

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    // The native DEVMODE union is 16 bytes because of its printer-format members.
    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct DISPLAYUNION
    {
        [FieldOffset(0)] public int X;
        [FieldOffset(4)] public int Y;
        [FieldOffset(0)] public uint Orientation;
        [FieldOffset(4)] public uint FixedOutput;
        [FieldOffset(8)] public int PrinterUnion0;
        [FieldOffset(12)] public int PrinterUnion1;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public uint dmFields;
        public DISPLAYUNION dmPosition;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public uint dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2;
        public uint dmPanningWidth, dmPanningHeight;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT Monitor, Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public uint Type, Size;
        public LUID AdapterId;
        public uint Id;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_SOURCE_INFO
    {
        public LUID AdapterId;
        public uint Id, ModeInfoIndex, StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_RATIONAL { public uint Numerator, Denominator; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID AdapterId;
        public uint Id, ModeInfoIndex, OutputTechnology, Rotation, Scaling;
        public DISPLAYCONFIG_RATIONAL RefreshRate;
        public uint ScanLineOrdering, TargetAvailable, StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO SourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO TargetInfo;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER Header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_ADVANCED_COLOR_INFO
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER Header;
        public uint Value;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PHYSICAL_MONITOR
    {
        public IntPtr Handle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplayDevicesW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumDisplayDevices(string? device, uint number, ref DISPLAY_DEVICE displayDevice, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplaySettingsW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumDisplaySettings(string device, int modeNumber, ref DEVMODE devMode);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "ChangeDisplaySettingsExW", SetLastError = true)]
    private static extern int ChangeDisplaySettingsEx(string device, ref DEVMODE devMode, IntPtr hwnd, int flags, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFOEX info);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT point, uint flags);
    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr monitor, out uint count);
    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr monitor, uint count, [Out] PHYSICAL_MONITOR[] physicalMonitors);
    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyPhysicalMonitors(uint count, [In] PHYSICAL_MONITOR[] physicalMonitors);
    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorBrightness(IntPtr monitor, out uint minimum, out uint current, out uint maximum);
    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetMonitorBrightness(IntPtr monitor, uint newBrightness);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateDC(string driver, string device, string? output, IntPtr initData);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetDeviceGammaRamp(IntPtr dc, [Out] ushort[] ramp);
    [DllImport("gdi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetDeviceGammaRamp(IntPtr dc, [In] ushort[] ramp);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetICMProfileW")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetICMProfile(IntPtr dc, ref uint length, StringBuilder profileName);
    [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);
    [DllImport("user32.dll")] private static extern int QueryDisplayConfig(uint flags, ref uint numPathArrayElements, IntPtr pathInfoArray,
        ref uint numModeInfoArrayElements, IntPtr modeInfoArray, IntPtr currentTopologyId);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME requestPacket);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_ADVANCED_COLOR_INFO requestPacket);
}
