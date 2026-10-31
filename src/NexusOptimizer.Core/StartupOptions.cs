namespace NexusOptimizer.Core;

public enum RunMode
{
    Gui,
    Headless
}

/// <summary>
/// Kommandozeilen-Optionen. Bewusst ein reiner Parser ohne Seiteneffekte,
/// damit er ohne UI und ohne Rechte testbar ist.
///
///   --capture            Messung starten (Headless)
///   --duration &lt;s&gt;       Dauer in Sekunden (1..86400)
///   --game &lt;name&gt;        Spielname für die Session
///   --profile &lt;id&gt;       Profil anwenden
///   --export &lt;pfad&gt;      Ergebnis als JSON schreiben
///   --safe               sicherer Modus (keine Systemänderungen)
///   --silent             keine Ausgabe außer Ergebnis
///   --diagnose           Diagnosebericht erzeugen
/// </summary>
public sealed record StartupOptions(
    RunMode Mode,
    bool Safe,
    bool Silent,
    bool Diagnose,
    bool Capture,
    TimeSpan? Duration,
    string? Game,
    string? Profile,
    string? ExportPath)
{
    public static Result<StartupOptions> Parse(IReadOnlyList<string> args)
    {
        bool safe = false, silent = false, diagnose = false, capture = false;
        TimeSpan? duration = null;
        string? game = null, profile = null, export = null;
        bool headless = false;

        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "--safe":
                    safe = true;
                    break;
                case "--silent":
                    silent = true;
                    headless = true;
                    break;
                case "--diagnose":
                    diagnose = true;
                    headless = true;
                    break;
                case "--capture":
                    capture = true;
                    headless = true;
                    break;
                case "--duration":
                    if (!TryTakeValue(args, ref i, out string durationValue))
                        return Result<StartupOptions>.Fail("--duration benötigt einen Wert in Sekunden");
                    if (!int.TryParse(durationValue, out int seconds) || seconds is < 1 or > 86400)
                        return Result<StartupOptions>.Fail("--duration muss zwischen 1 und 86400 Sekunden liegen");
                    duration = TimeSpan.FromSeconds(seconds);
                    break;
                case "--game":
                    if (!TryTakeValue(args, ref i, out string gameValue))
                        return Result<StartupOptions>.Fail("--game benötigt einen Namen");
                    game = gameValue;
                    break;
                case "--profile":
                    if (!TryTakeValue(args, ref i, out string profileValue))
                        return Result<StartupOptions>.Fail("--profile benötigt eine ID");
                    profile = profileValue;
                    break;
                case "--export":
                    if (!TryTakeValue(args, ref i, out string exportValue))
                        return Result<StartupOptions>.Fail("--export benötigt einen Pfad");
                    export = exportValue;
                    break;
                default:
                    return Result<StartupOptions>.Fail($"Unbekannte Option: {arg}");
            }
        }

        if (capture && duration is null)
            return Result<StartupOptions>.Fail("--capture benötigt --duration");

        if (capture && safe)
            return Result<StartupOptions>.Fail("--capture und --safe schließen sich aus");

        return Result<StartupOptions>.Ok(new StartupOptions(
            Mode: headless ? RunMode.Headless : RunMode.Gui,
            Safe: safe,
            Silent: silent,
            Diagnose: diagnose,
            Capture: capture,
            Duration: duration,
            Game: game,
            Profile: profile,
            ExportPath: export));
    }

    private static bool TryTakeValue(IReadOnlyList<string> args, ref int index, out string value)
    {
        if (index + 1 >= args.Count || args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            value = string.Empty;
            return false;
        }

        value = args[++index];
        return true;
    }
}
