using NexusOptimizer.Core;
using NexusOptimizer.Core.Snapshots;
using Xunit;

namespace NexusOptimizer.Core.Tests;

public class SnapshotTests
{
    private static SnapshotDocument CreateDocument(params SnapshotEntry[] entries) =>
        new(SnapshotDocument.CurrentSchemaVersion, Guid.NewGuid(), DateTimeOffset.UtcNow,
            "Test-Snapshot", entries);

    [Fact]
    public void Roundtrip_PreservesContentAndChecksum()
    {
        var document = CreateDocument(
            new SnapshotEntry(SnapshotTargetKind.RegistryValue, @"HKCU\Software\Test", "Wert", "1", true),
            new SnapshotEntry(SnapshotTargetKind.ServiceStartType, "DiagTrack", null, "auto", true));

        var json = SnapshotCodec.Serialize(document);
        var parsed = SnapshotCodec.Deserialize(json);

        Assert.True(parsed.IsSuccess);
        Assert.Equal(document.Id, parsed.Value!.Id);
        Assert.Equal(2, parsed.Value.Entries.Count);
    }

    [Fact]
    public void TamperedSnapshot_IsRejected()
    {
        var document = CreateDocument(
            new SnapshotEntry(SnapshotTargetKind.RegistryValue, @"HKCU\Software\Test", "Wert", "1", true));

        var json = SnapshotCodec.Serialize(document);
        string tampered = json.Replace("\"1\"", "\"0\"");

        var parsed = SnapshotCodec.Deserialize(tampered);

        Assert.False(parsed.IsSuccess);
        Assert.Contains("Prüfsumme", parsed.Error);
    }

    [Fact]
    public void UnknownSchemaVersion_IsRejected()
    {
        var document = CreateDocument() with { SchemaVersion = 99 };
        var parsed = SnapshotCodec.Deserialize(SnapshotCodec.Serialize(document));

        Assert.False(parsed.IsSuccess);
    }

    [Fact]
    public void Checksum_ChangesWithContent()
    {
        var a = CreateDocument(new SnapshotEntry(SnapshotTargetKind.PowerPlan, "SCHEME_BALANCED", null, null, true));
        var b = CreateDocument(new SnapshotEntry(SnapshotTargetKind.PowerPlan, "SCHEME_BALANCED", null, null, false));

        Assert.NotEqual(a.ComputeChecksum(), b.ComputeChecksum());
    }

    [Fact]
    public void RollbackPlan_RunsInReverseOrder()
    {
        var document = CreateDocument(
            new SnapshotEntry(SnapshotTargetKind.RegistryValue, @"HKCU\A", "x", "1", true),
            new SnapshotEntry(SnapshotTargetKind.ServiceStartType, "Svc", null, "auto", true),
            new SnapshotEntry(SnapshotTargetKind.PowerPlan, "plan", null, "balanced", true));

        var steps = RollbackPlanner.Plan(document);

        Assert.Equal(3, steps.Count);
        Assert.Equal(RollbackOperation.RestorePowerPlan, steps[0].Operation);
        Assert.Equal(RollbackOperation.RestoreValue, steps[2].Operation);
    }

    [Fact]
    public void RollbackPlan_DeletesValuesThatDidNotExist()
    {
        var document = CreateDocument(
            new SnapshotEntry(SnapshotTargetKind.RegistryValue, @"HKCU\B", "neu", null, false));

        var step = Assert.Single(RollbackPlanner.Plan(document));

        Assert.Equal(RollbackOperation.DeleteValue, step.Operation);
        Assert.Contains("entfernen", step.Description);
    }

    [Fact]
    public void Preview_ContainsSnapshotIdAndSteps()
    {
        var document = CreateDocument(
            new SnapshotEntry(SnapshotTargetKind.RegistryValue, @"HKCU\C", "y", "2", true));

        string preview = RollbackPlanner.DescribePreview(document);

        Assert.Contains(document.Id.ToString(), preview);
        Assert.Contains("HKCU\\C", preview);
    }
}

public class WriteAheadJournalTests
{
    [Fact]
    public void CompletedPlan_NeedsNoRollback()
    {
        var journal = new WriteAheadJournal();
        var id = Guid.NewGuid();

        journal.Prepare(id, "gamebar");
        journal.MarkApplied(id, "gamebar");
        journal.CloseAll(id);

        Assert.Empty(journal.GetPendingRollbackActions(id));
        Assert.Empty(journal.GetSnapshotsWithPendingWork());
    }

    [Fact]
    public void AppliedWithoutClose_NeedsRollback()
    {
        var journal = new WriteAheadJournal();
        var id = Guid.NewGuid();

        journal.Prepare(id, "hags");
        journal.MarkApplied(id, "hags");

        Assert.Equal(new[] { "hags" }, journal.GetPendingRollbackActions(id));
        Assert.Contains(id, journal.GetSnapshotsWithPendingWork());
    }

    [Fact]
    public void PreparedOnly_IsTreatedAsPending()
    {
        var journal = new WriteAheadJournal();
        var id = Guid.NewGuid();
        journal.Prepare(id, "tcp");

        Assert.Equal(new[] { "tcp" }, journal.GetPendingRollbackActions(id));
    }

    [Fact]
    public void FailedAction_IsNotRolledBackButKeepsSnapshotOpen()
    {
        var journal = new WriteAheadJournal();
        var id = Guid.NewGuid();
        journal.Prepare(id, "xbox");
        journal.MarkFailed(id, "xbox", "access denied");

        Assert.Empty(journal.GetPendingRollbackActions(id));
        Assert.Contains(id, journal.GetSnapshotsWithPendingWork());
    }

    [Fact]
    public void Replay_RestoresFromDisk()
    {
        var journal = new WriteAheadJournal();
        var id = Guid.NewGuid();
        journal.Prepare(id, "a");
        journal.MarkApplied(id, "a");

        var replayed = new WriteAheadJournal();
        replayed.Replay(journal.Records);

        Assert.Equal(new[] { "a" }, replayed.GetPendingRollbackActions(id));
    }
}

public class StartupOptionsTests
{
    [Fact]
    public void EmptyArgs_StartGui()
    {
        var result = StartupOptions.Parse(Array.Empty<string>());

        Assert.True(result.IsSuccess);
        Assert.Equal(RunMode.Gui, result.Value!.Mode);
    }

    [Fact]
    public void SafeAndCapture_ParseCorrectly()
    {
        var result = StartupOptions.Parse(new[] { "--safe", "--diagnose" });

        Assert.True(result.IsSuccess);
        Assert.True(result.Value!.Safe);
        Assert.True(result.Value.Diagnose);
        Assert.Equal(RunMode.Headless, result.Value.Mode);
    }

    [Fact]
    public void CaptureWithoutDuration_Fails()
    {
        var result = StartupOptions.Parse(new[] { "--capture" });

        Assert.False(result.IsSuccess);
        Assert.Contains("--duration", result.Error);
    }

    [Fact]
    public void InvalidDuration_Fails()
    {
        Assert.False(StartupOptions.Parse(new[] { "--capture", "--duration", "0" }).IsSuccess);
        Assert.False(StartupOptions.Parse(new[] { "--capture", "--duration", "abc" }).IsSuccess);
    }

    [Fact]
    public void CaptureAndSafe_AreContradictory()
    {
        var result = StartupOptions.Parse(new[] { "--capture", "--duration", "60", "--safe" });

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void FullCommandLine_ParsesAllValues()
    {
        var result = StartupOptions.Parse(new[]
        {
            "--capture", "--duration", "120", "--game", "Cyberpunk 2077",
            "--profile", "competitive", "--export", @"C:\exports\run.json", "--silent"
        });

        Assert.True(result.IsSuccess);
        var options = result.Value!;
        Assert.Equal(TimeSpan.FromSeconds(120), options.Duration);
        Assert.Equal("Cyberpunk 2077", options.Game);
        Assert.Equal("competitive", options.Profile);
        Assert.EndsWith("run.json", options.ExportPath);
        Assert.True(options.Silent);
    }

    [Fact]
    public void UnknownOption_Fails()
    {
        var result = StartupOptions.Parse(new[] { "--turbo" });

        Assert.False(result.IsSuccess);
        Assert.Contains("--turbo", result.Error);
    }
}
