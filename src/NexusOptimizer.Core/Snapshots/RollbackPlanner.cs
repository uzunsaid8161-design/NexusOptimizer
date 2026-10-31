namespace NexusOptimizer.Core.Snapshots;

public enum RollbackOperation
{
    RestoreValue,
    DeleteValue,
    RestoreServiceStartType,
    RestorePowerPlan
}

public sealed record RollbackStep(RollbackOperation Operation, SnapshotEntry Entry, string Description);

/// <summary>
/// Erzeugt die Rücknahme-Reihenfolge: umgekehrt zur Anwendung, damit abhängige
/// Änderungen (z. B. Dienst-Starttyp vor Dienst-Löschung) korrekt zurücklaufen.
/// </summary>
public static class RollbackPlanner
{
    public static IReadOnlyList<RollbackStep> Plan(SnapshotDocument document)
    {
        var steps = new List<RollbackStep>(document.Entries.Count);

        for (int i = document.Entries.Count - 1; i >= 0; i--)
        {
            var entry = document.Entries[i];
            (RollbackOperation op, string description) = entry.Kind switch
            {
                SnapshotTargetKind.RegistryValue => entry.Existed
                    ? (RollbackOperation.RestoreValue, $"Registry: {entry.Path}\\{entry.Name} = {entry.PreviousValue ?? "(leer)"}")
                    : (RollbackOperation.DeleteValue, $"Registry: {entry.Path}\\{entry.Name} entfernen"),
                SnapshotTargetKind.ServiceStartType =>
                    (RollbackOperation.RestoreServiceStartType, $"Dienst {entry.Path}: Starttyp = {entry.PreviousValue}"),
                SnapshotTargetKind.PowerPlan =>
                    (RollbackOperation.RestorePowerPlan, $"Energieplan: {entry.PreviousValue}"),
                SnapshotTargetKind.ApplicationSetting => entry.Existed
                    ? (RollbackOperation.RestoreValue, $"Einstellung {entry.Path} = {entry.PreviousValue}")
                    : (RollbackOperation.DeleteValue, $"Einstellung {entry.Path} entfernen"),
                _ => (RollbackOperation.RestoreValue, $"Unbekannt: {entry.Path}")
            };

            steps.Add(new RollbackStep(op, entry, description));
        }

        return steps;
    }

    /// <summary>Vorschau ohne Ausführung - für den Bestätigungsdialog.</summary>
    public static string DescribePreview(SnapshotDocument document)
    {
        var steps = Plan(document);
        var lines = new List<string>
        {
            $"Snapshot {document.Id} vom {document.CreatedUtc:dd.MM.yyyy HH:mm}",
            $"{steps.Count} Schritte werden zurückgenommen:",
            string.Empty
        };
        lines.AddRange(steps.Select(s => "  • " + s.Description));
        return string.Join(Environment.NewLine, lines);
    }
}
