namespace NexusOptimizer.Core.Snapshots;

public enum JournalState
{
    Prepared,
    Applied,
    Failed,
    Closed,
    RolledBack
}

public sealed record JournalRecord(
    Guid SnapshotId,
    string ActionId,
    JournalState State,
    DateTimeOffset Utc,
    string? Detail = null);

/// <summary>
/// Write-Ahead-Journal für Optimierungen. Vor jeder Systemänderung wird "Prepared"
/// geschrieben, nach Erfolg "Applied". Erst wenn der gesamte Plan durch ist, wird
/// "Closed" gesetzt. Beim nächsten Start zeigen offene Einträge, dass ein Vorgang
/// abgebrochen wurde - dann wird zuerst zurückgerollt, bevor die UI interaktiv wird.
/// </summary>
public sealed class WriteAheadJournal
{
    private readonly List<JournalRecord> _records = new();
    private readonly object _sync = new();

    public IReadOnlyList<JournalRecord> Records
    {
        get { lock (_sync) return _records.ToArray(); }
    }

    public void Prepare(Guid snapshotId, string actionId) =>
        Append(new JournalRecord(snapshotId, actionId, JournalState.Prepared, DateTimeOffset.UtcNow));

    public void MarkApplied(Guid snapshotId, string actionId) =>
        Append(new JournalRecord(snapshotId, actionId, JournalState.Applied, DateTimeOffset.UtcNow));

    public void MarkFailed(Guid snapshotId, string actionId, string reason) =>
        Append(new JournalRecord(snapshotId, actionId, JournalState.Failed, DateTimeOffset.UtcNow, reason));

    public void CloseAll(Guid snapshotId) =>
        Append(new JournalRecord(snapshotId, "*", JournalState.Closed, DateTimeOffset.UtcNow));

    public void MarkRolledBack(Guid snapshotId) =>
        Append(new JournalRecord(snapshotId, "*", JournalState.RolledBack, DateTimeOffset.UtcNow));

    private void Append(JournalRecord record)
    {
        lock (_sync) _records.Add(record);
    }

    /// <summary>
    /// Aktionen, die angewendet (oder unklar vorbereitet) wurden, ohne dass der
    /// Vorgang sauber abgeschlossen wurde. Diese müssen beim nächsten Start
    /// zurückgenommen werden.
    /// </summary>
    public IReadOnlyList<string> GetPendingRollbackActions(Guid snapshotId)
    {
        lock (_sync)
        {
            var relevant = _records.Where(r => r.SnapshotId == snapshotId).ToList();
            if (relevant.Any(r => r.State is JournalState.Closed or JournalState.RolledBack))
                return Array.Empty<string>();

            return relevant
                .GroupBy(r => r.ActionId)
                .Select(g => g.Last())
                .Where(r => r.State is JournalState.Applied or JournalState.Prepared)
                .Select(r => r.ActionId)
                .Distinct()
                .ToArray();
        }
    }

    /// <summary>Snapshots, die einen unvollständigen Vorgang haben.</summary>
    public IReadOnlyList<Guid> GetSnapshotsWithPendingWork()
    {
        lock (_sync)
        {
            return _records
                .GroupBy(r => r.SnapshotId)
                .Where(g => !g.Any(r => r.State is JournalState.Closed or JournalState.RolledBack))
                .Where(g => g.Any(r => r.State is JournalState.Applied or JournalState.Prepared))
                .Select(g => g.Key)
                .ToArray();
        }
    }

    public void Replay(IEnumerable<JournalRecord> records)
    {
        lock (_sync)
        {
            _records.Clear();
            _records.AddRange(records);
        }
    }
}
