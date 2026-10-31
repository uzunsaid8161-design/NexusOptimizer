using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;
using NexusOptimizer.Models;

namespace NexusOptimizer.Services;

/// <summary>
/// Wendet Windows-Tweaks an und protokolliert jede Änderung mit Vorher-Wert in SQLite.
/// Alles mit try/catch: ohne Adminrechte bleibt das System unverändert, die App läuft weiter.
/// </summary>
public sealed class OptimizerService
{
    private const string GameDvrKey = @"Software\Microsoft\Windows\CurrentVersion\GameDVR";
    private const string GameConfigKey = @"System\GameConfigStore";
    private const string GraphicsDriversKey = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";
    private const string VisualFxKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects";

    private readonly Database _db;
    private readonly MemoryOptimizer _memory = new();
    private readonly string _snapshotPath = Path.Combine(AppPaths.Root, "optimizer-snapshots.json");
    private readonly object _snapshotGate = new();
    private readonly Dictionary<string, List<RegistrySnapshot>> _registrySnapshots = new(StringComparer.OrdinalIgnoreCase);
    private bool _snapshotLoadFailed;

    private sealed record RegistryTarget(bool Machine, string Path, string Name);
    private sealed record RegistrySnapshot(bool Machine, string Path, string Name, bool Exists, string Kind, string? Value);

    public bool IsAdmin { get; }
    public IReadOnlyList<OptimizationItem> Items { get; }

    public bool HasRollbackSnapshot(string key)
    {
        lock (_snapshotGate) return !_snapshotLoadFailed && _registrySnapshots.ContainsKey(key);
    }

    public OptimizerService(Database db)
    {
        _db = db;
        LoadRegistrySnapshots();
        IsAdmin = CheckAdmin();
        Items = BuildItems();
    }

    private static bool CheckAdmin()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static List<OptimizationItem> BuildItems() => new()
    {
        new OptimizationItem
        {
            Key = "prio", Title = "Spiel-Priorität: Hoch", Risk = "Niedrig",
            Description = "Setzt den erkannten Spiel-Prozess auf hohe CPU-Priorität."
        },
        new OptimizationItem
        {
            Key = "gamebar", Title = "Game Bar / Game DVR aus", Risk = "Niedrig",
            SupportsRollback = true,
            Description = "Deaktiviert Aufnahme, Hintergrundaufnahme und das Xbox-Overlay (HKCU)."
        },
        new OptimizationItem
        {
            Key = "visualfx", Title = "Visuelle Effekte: Beste Leistung", Risk = "Niedrig",
            SupportsRollback = true,
            Description = "Schaltet Windows-Animationen und Transparenz-Effekte ab (HKCU)."
        },
        new OptimizationItem
        {
            Key = "power", Title = "Gaming-Energieplan", Risk = "Mittel", NeedsAdmin = true,
            Description = "Aktiviert den Energieplan „Höchstleistung“, der vorherige Plan wird gemerkt."
        },
        new OptimizationItem
        {
            Key = "hags", Title = "Hardware-GPU-Scheduling", Risk = "Mittel", NeedsAdmin = true,
            SupportsRollback = true,
            Description = "Setzt HwSchMode = 2. Wirkt erst nach einem Neustart."
        },
        new OptimizationItem
        {
            Key = "diag", Title = "Telemetrie pausieren", Risk = "Mittel", NeedsAdmin = true,
            Description = "Stoppt den Dienst DiagTrack und setzt den Starttyp auf „Deaktiviert“."
        },
        new OptimizationItem
        {
            Key = "xbox", Title = "Xbox-Dienste aus", Risk = "Niedrig", NeedsAdmin = true,
            Description = "Deaktiviert XblGameSave und XboxNetApiSvc."
        },
        new OptimizationItem
        {
            Key = "tcp", Title = "Netzwerk: Nagle/TCP-Tuning", Risk = "Mittel", NeedsAdmin = true,
            Description = "Setzt TCP-Autotuning und RSS für geringere Latenz."
        },
        new OptimizationItem
        {
            Key = "cpusleep", Title = "CPU-Sleep-Modus aus", Risk = "Mittel", NeedsAdmin = true,
            Description = "Erzwingt 100 % Mindest-Takt im aktiven Energieplan (kein Heruntertakten im Spiel)."
        },
        new OptimizationItem
        {
            Key = "cleanram", Title = "RAM aufräumen", Risk = "Niedrig",
            IsOneShot = true,
            Description = "Trimmt Working Sets erreichbarer Prozesse; Standby-Liste nur mit Adminrechten."
        },
        new OptimizationItem
        {
            Key = "clipboard", Title = "Zwischenablage leeren", Risk = "Niedrig",
            IsOneShot = true,
            Description = "Löscht den aktuellen Inhalt der Zwischenablage (sofort, nicht umkehrbar)."
        },
        new OptimizationItem
        {
            Key = "bgapps", Title = "Hintergrund-Apps aus", Risk = "Niedrig",
            SupportsRollback = true,
            Description = "Verbietet Windows-Store-Apps die Ausführung im Hintergrund (HKCU)."
        },
        new OptimizationItem
        {
            Key = "sticky", Title = "Sticky Keys aus", Risk = "Niedrig",
            SupportsRollback = true,
            Description = "Deaktiviert die Einrastfunktion, die im Spiel Dialoge auslösen kann (HKCU)."
        },
        new OptimizationItem
        {
            Key = "cortana", Title = "Cortana aus", Risk = "Mittel", NeedsAdmin = true,
            SupportsRollback = true,
            Description = "Setzt die Cortana-Richtlinie auf deaktiviert (HKLM)."
        },
        new OptimizationItem
        {
            Key = "wer", Title = "Problemberichte aus", Risk = "Niedrig", NeedsAdmin = true,
            SupportsRollback = true,
            Description = "Deaktiviert Windows Error Reporting, damit keine Crash-Dumps im Hintergrund laufen."
        }
    };

    public string Apply(OptimizationItem item, bool enable)
    {
        string previous = ReadPrevious(item.Key);
        string message;

        if (item.IsOneShot && !enable)
            return "Einmalaktionen werden nicht als Schalter zurückgesetzt.";

        if (item.SupportsRollback)
        {
            if (enable)
            {
                var snapshot = EnsureRegistrySnapshot(item.Key);
                if (!snapshot.Success)
                {
                    _db.RecordOptimization(item.Key, item.Title, false, snapshot.Message);
                    return snapshot.Message;
                }
                previous = snapshot.Serialized ?? previous;
            }
            else
            {
                message = RestoreRegistrySnapshot(item.Key);
                _db.RecordOptimization(item.Key, item.Title, false, previous);
                return message;
            }
        }

        try
        {
            message = item.Key switch
            {
                "prio" => ApplyPriority(enable),
                "gamebar" => ApplyGameBar(enable),
                "visualfx" => ApplyVisualFx(enable),
                "power" => Run("powercfg", enable ? "/setactive SCHEME_MIN" : "/setactive SCHEME_BALANCED") == 0
                    ? (enable ? "Energieplan: Höchstleistung." : "Energieplan: Ausbalanciert.")
                    : "Adminrechte erforderlich.",
                "hags" => SetHklm(GraphicsDriversKey, "HwSchMode", enable ? 2 : 1)
                    ? (enable ? "HAGS aktiviert (Neustart nötig)." : "HAGS deaktiviert.")
                    : "Adminrechte erforderlich.",
                "diag" => ApplyService("DiagTrack", enable),
                "xbox" => ApplyService("XblGameSave", enable, "XboxNetApiSvc"),
                "tcp" => ApplyTcp(enable),
                "cpusleep" => Run("powercfg", enable
                        ? "/setacvalueindex SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMIN 100"
                        : "/setacvalueindex SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMIN 5") == 0
                    && Run("powercfg", "/setactive SCHEME_CURRENT") == 0
                        ? (enable ? "Mindesttakt auf 100 % gesetzt." : "Mindesttakt zurückgesetzt (5 %).")
                        : "Adminrechte erforderlich.",
                "cleanram" => _memory.Release(intensive: true).Message,
                "clipboard" => ClearClipboard() ? "Zwischenablage geleert." : "Zwischenablage konnte nicht geleert werden.",
                "bgapps" => SetHkcu(@"Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications", "GlobalUserDisabled", enable ? 1 : 0)
                    ? (enable ? "Hintergrund-Apps deaktiviert." : "Hintergrund-Apps wieder erlaubt.")
                    : "HKCU-Zugriff verweigert.",
                "sticky" => SetHkcu(@"Control Panel\Accessibility\StickyKeys", "Flags", enable ? "506" : "510")
                    ? (enable ? "Sticky Keys deaktiviert." : "Sticky Keys aktiviert.")
                    : "HKCU-Zugriff verweigert.",
                "cortana" => SetHklm(@"SOFTWARE\Policies\Microsoft\Windows\Windows Search", "AllowCortana", enable ? 0 : 1)
                    ? (enable ? "Cortana-Richtlinie deaktiviert." : "Cortana-Richtlinie aktiviert.")
                    : "Adminrechte erforderlich.",
                "wer" => SetHklm(@"SOFTWARE\Microsoft\Windows\Windows Error Reporting", "Disabled", enable ? 1 : 0)
                    ? (enable ? "Fehlerberichte deaktiviert." : "Fehlerberichte aktiviert.")
                    : "Adminrechte erforderlich.",
                _ => "Unbekannter Schlüssel."
            };
        }
        catch (UnauthorizedAccessException)
        {
            message = "Adminrechte erforderlich.";
        }
        catch (Exception ex)
        {
            message = "Fehler: " + ex.Message;
        }

        _db.RecordOptimization(item.Key, item.Title, enable, previous);
        return message;
    }

    private static string ApplyPriority(bool enable)
    {
        var process = ForegroundProcess();
        if (process is null) return "Kein Spielfenster im Vordergrund.";
        process.PriorityClass = enable ? ProcessPriorityClass.High : ProcessPriorityClass.Normal;
        return enable ? $"Priorität Hoch: {process.ProcessName}" : $"Priorität Normal: {process.ProcessName}";
    }

    private static string ApplyGameBar(bool enable)
    {
        bool capture = SetHkcu(GameDvrKey, "AppCaptureEnabled", enable ? 0 : 1);
        bool store = SetHkcu(GameConfigKey, "GameDVR_Enabled", enable ? 0 : 1);
        Serilog.Log.Information("GameBar: AppCapture={Cap} GameDVR={Store} -> {Enable}", capture, store, enable);
        if (!capture && !store) return "Registry-Zugriff auf HKCU verweigert.";
        return enable ? "Game DVR deaktiviert." : "Game DVR aktiviert.";
    }

    private static string ApplyVisualFx(bool enable)
    {
        return SetHkcu(VisualFxKey, "VisualFXSetting", enable ? 2 : 0)
            ? (enable ? "Visuelle Effekte: Beste Leistung." : "Visuelle Effekte: Windows-Standard.")
            : "Zugriff auf HKCU verweigert.";
    }

    private static string ApplyTcp(bool enable)
    {
        int autotuning = Run("netsh", "int tcp set global autotuninglevel=" + (enable ? "normal" : "restricted"));
        int rss = Run("netsh", "int tcp set global rss=" + (enable ? "enabled" : "disabled"));
        return autotuning == 0 || rss == 0
            ? (enable ? "Netzwerk-Tuning aktiv." : "Netzwerk-Tuning zurückgesetzt.")
            : "Adminrechte erforderlich.";
    }

    private static string ApplyService(string name, bool enable, string? second = null)
    {
        int first = Run("sc", $"config {name} start= {(enable ? "disabled" : "demand")}");
        if (first == 0) Run("sc", (enable ? "stop " : "start ") + name);
        if (second is not null)
        {
            Run("sc", $"config {second} start= {(enable ? "disabled" : "demand")}");
        }

        return first == 0
            ? (enable ? $"{name} deaktiviert." : $"{name} reaktiviert.")
            : "Adminrechte erforderlich.";
    }

    private string ReadPrevious(string key) => key switch
    {
        "gamebar" => ReadHkcu(GameDvrKey, "AppCaptureEnabled") ?? "unbekannt",
        "visualfx" => ReadHkcu(VisualFxKey, "VisualFXSetting") ?? "unbekannt",
        "hags" => ReadHklm(GraphicsDriversKey, "HwSchMode") ?? "unbekannt",
        _ => "unbekannt"
    };

    private static IReadOnlyList<RegistryTarget> RegistryTargets(string key) => key switch
    {
        "gamebar" => new[]
        {
            new RegistryTarget(false, GameDvrKey, "AppCaptureEnabled"),
            new RegistryTarget(false, GameConfigKey, "GameDVR_Enabled")
        },
        "visualfx" => new[] { new RegistryTarget(false, VisualFxKey, "VisualFXSetting") },
        "hags" => new[] { new RegistryTarget(true, GraphicsDriversKey, "HwSchMode") },
        "bgapps" => new[] { new RegistryTarget(false, @"Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications", "GlobalUserDisabled") },
        "sticky" => new[] { new RegistryTarget(false, @"Control Panel\Accessibility\StickyKeys", "Flags") },
        "cortana" => new[] { new RegistryTarget(true, @"SOFTWARE\Policies\Microsoft\Windows\Windows Search", "AllowCortana") },
        "wer" => new[] { new RegistryTarget(true, @"SOFTWARE\Microsoft\Windows\Windows Error Reporting", "Disabled") },
        _ => Array.Empty<RegistryTarget>()
    };

    private void LoadRegistrySnapshots()
    {
        if (!File.Exists(_snapshotPath)) return;
        try
        {
            var loaded = JsonSerializer.Deserialize<Dictionary<string, List<RegistrySnapshot>>>(File.ReadAllText(_snapshotPath));
            if (loaded is null) throw new InvalidDataException("Snapshotdatei ist leer.");
            foreach (var (key, values) in loaded) _registrySnapshots[key] = values;
        }
        catch (Exception ex)
        {
            _snapshotLoadFailed = true;
            Serilog.Log.Error(ex, "Optimizer-Sicherungen konnten nicht gelesen werden");
        }
    }

    private (bool Success, string Message, string? Serialized) EnsureRegistrySnapshot(string key)
    {
        lock (_snapshotGate)
        {
            if (_snapshotLoadFailed)
                return (false, "Die Sicherungsdatei ist beschädigt. Der Registry-Tweak wurde aus Sicherheitsgründen nicht angewendet.", null);

            if (_registrySnapshots.TryGetValue(key, out var existing))
                return (true, string.Empty, JsonSerializer.Serialize(existing));

            try
            {
                var captured = RegistryTargets(key).Select(CaptureRegistryValue).ToList();
                _registrySnapshots[key] = captured;
                SaveRegistrySnapshots();
                return (true, string.Empty, JsonSerializer.Serialize(captured));
            }
            catch (Exception ex)
            {
                _registrySnapshots.Remove(key);
                Serilog.Log.Warning(ex, "Optimizer-Snapshot für {Key} konnte nicht gespeichert werden", key);
                return (false, "Vor der Änderung konnte keine Sicherung geschrieben werden: " + ex.Message, null);
            }
        }
    }

    private string RestoreRegistrySnapshot(string key)
    {
        lock (_snapshotGate)
        {
            if (_snapshotLoadFailed)
                return "Die Sicherungsdatei ist beschädigt. Es wurde nichts zurückgesetzt.";
            if (!_registrySnapshots.TryGetValue(key, out var snapshots))
                return "Kein ursprünglicher Wert gespeichert. Es wurde nichts geraten oder geändert.";

            try
            {
                foreach (var snapshot in snapshots) RestoreRegistryValue(snapshot);
                _registrySnapshots.Remove(key);
                try { SaveRegistrySnapshots(); }
                catch
                {
                    _registrySnapshots[key] = snapshots;
                    throw;
                }
                return "Gesicherte ursprüngliche Windows-Werte wiederhergestellt.";
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Optimizer-Rollback für {Key} unvollständig", key);
                return "Rollback unvollständig; die Sicherung bleibt für einen erneuten Versuch erhalten: " + ex.Message;
            }
        }
    }

    private static RegistrySnapshot CaptureRegistryValue(RegistryTarget target)
    {
        using var baseKey = RegistryKey.OpenBaseKey(target.Machine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, RegistryView.Default);
        using var key = baseKey.OpenSubKey(target.Path);
        if (key is null || !key.GetValueNames().Contains(target.Name, StringComparer.OrdinalIgnoreCase))
            return new RegistrySnapshot(target.Machine, target.Path, target.Name, false, string.Empty, null);

        var kind = key.GetValueKind(target.Name);
        if (kind is not (RegistryValueKind.String or RegistryValueKind.ExpandString or RegistryValueKind.DWord or RegistryValueKind.QWord))
            throw new InvalidDataException($"Registry-Typ {kind} wird für Sicherungen nicht unterstützt.");

        object? value = key.GetValue(target.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return new RegistrySnapshot(target.Machine, target.Path, target.Name, true, kind.ToString(),
            Convert.ToString(value, CultureInfo.InvariantCulture));
    }

    private static void RestoreRegistryValue(RegistrySnapshot snapshot)
    {
        using var baseKey = RegistryKey.OpenBaseKey(snapshot.Machine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, RegistryView.Default);
        if (!snapshot.Exists)
        {
            using var existing = baseKey.OpenSubKey(snapshot.Path, writable: true);
            existing?.DeleteValue(snapshot.Name, throwOnMissingValue: false);
            return;
        }

        if (!Enum.TryParse(snapshot.Kind, out RegistryValueKind kind))
            throw new InvalidDataException("Unbekannter Registry-Typ: " + snapshot.Kind);
        using var key = baseKey.CreateSubKey(snapshot.Path, writable: true)
            ?? throw new UnauthorizedAccessException("Registry-Schlüssel konnte nicht geöffnet werden.");
        object value = kind switch
        {
            RegistryValueKind.String or RegistryValueKind.ExpandString => snapshot.Value ?? string.Empty,
            RegistryValueKind.DWord => int.Parse(snapshot.Value ?? "0", CultureInfo.InvariantCulture),
            RegistryValueKind.QWord => long.Parse(snapshot.Value ?? "0", CultureInfo.InvariantCulture),
            _ => throw new InvalidDataException("Nicht unterstützter Registry-Typ: " + kind)
        };
        key.SetValue(snapshot.Name, value, kind);
    }

    private void SaveRegistrySnapshots()
    {
        Directory.CreateDirectory(AppPaths.Root);
        string temporary = _snapshotPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_registrySnapshots, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, _snapshotPath, overwrite: true);
    }

    private static Process? ForegroundProcess()
    {
        try
        {
            var handle = GetForegroundWindow();
            if (handle == IntPtr.Zero) return null;
            GetWindowThreadProcessId(handle, out uint pid);
            return pid == 0 ? null : Process.GetProcessById((int)pid);
        }
        catch
        {
            return null;
        }
    }

    private static bool SetHkcu(string path, string name, object value)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(path);
            key?.SetValue(name, value);
            return key is not null;
        }
        catch { return false; }
    }

    private static bool SetHklm(string path, string name, object value)
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(path);
            key?.SetValue(name, value);
            return key is not null;
        }
        catch { return false; }
    }

    private static string? ReadHkcu(string path, string name)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(path);
            return key?.GetValue(name)?.ToString();
        }
        catch { return null; }
    }

    private static string? ReadHklm(string path, string name)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(path);
            return key?.GetValue(name)?.ToString();
        }
        catch { return null; }
    }

    private static int Run(string file, string args)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(file, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (process is null) return -1;
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit(8000);
            return process.HasExited ? process.ExitCode : -1;
        }
        catch
        {
            return -1;
        }
    }

    private static bool ClearClipboard()
    {
        try
        {
            if (!OpenClipboard(IntPtr.Zero)) return false;
            EmptyClipboard();
            CloseClipboard();
            return true;
        }
        catch { return false; }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}
