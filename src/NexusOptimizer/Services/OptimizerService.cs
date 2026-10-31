using System.Diagnostics;
using System.Security.Principal;
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

    public bool IsAdmin { get; }
    public IReadOnlyList<OptimizationItem> Items { get; }

    public OptimizerService(Database db)
    {
        _db = db;
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
            Description = "Deaktiviert Aufnahme, Hintergrundaufnahme und das Xbox-Overlay (HKCU)."
        },
        new OptimizationItem
        {
            Key = "visualfx", Title = "Visuelle Effekte: Beste Leistung", Risk = "Niedrig",
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
        }
    };

    public string Apply(OptimizationItem item, bool enable)
    {
        string previous = ReadPrevious(item.Key);
        string message;

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

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}
