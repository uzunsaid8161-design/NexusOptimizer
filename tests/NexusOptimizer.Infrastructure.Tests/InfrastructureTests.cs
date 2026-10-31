using NexusOptimizer.Core.Snapshots;
using NexusOptimizer.Infrastructure;
using NexusOptimizer.Infrastructure.Migrations;
using NexusOptimizer.Infrastructure.Repositories;
using NexusOptimizer.Infrastructure.Retention;
using Xunit;

namespace NexusOptimizer.Infrastructure.Tests;

/// <summary>Jeder Test bekommt sein eigenes temporäres Verzeichnis mit echter SQLite-Datei.</summary>
internal sealed class TestDatabase : IDisposable
{
    public TestDatabase()
    {
        // Bewusst unter dem Build-Ausgabeverzeichnis: %TEMP% kann in eingeschränkten
        // Umgebungen gesperrt sein, das Ausgabeverzeichnis ist immer schreibbar.
        Root = Path.Combine(AppContext.BaseDirectory, "testdata", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Database = new SqliteDatabase(Path.Combine(Root, "nexus.db"));
        Backups = Path.Combine(Root, "backups");
    }

    public string Root { get; }
    public SqliteDatabase Database { get; }
    public string Backups { get; }

    public MigrationRunner Runner(IReadOnlyList<Migration>? migrations = null) =>
        new(Database, Backups, migrations);

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch { /* Testdaten */ }
    }
}

public class MigrationTests
{
    [Fact]
    public async Task Migrate_CreatesSchemaAndReachesLatestVersion()
    {
        using var fixture = new TestDatabase();
        var runner = fixture.Runner();

        var outcome = await runner.MigrateAsync();

        Assert.Equal(0, outcome.FromVersion);
        Assert.Equal(MigrationCatalog.All.Max(m => m.Version), outcome.ToVersion);
        Assert.Equal(outcome.ToVersion, await runner.GetCurrentVersionAsync());
        Assert.NotEmpty(outcome.Applied);
    }

    [Fact]
    public async Task Migrate_IsIdempotent()
    {
        using var fixture = new TestDatabase();
        var runner = fixture.Runner();
        await runner.MigrateAsync();

        var second = await runner.MigrateAsync();

        Assert.Empty(second.Applied);
        Assert.Equal(second.FromVersion, second.ToVersion);
    }

    [Fact]
    public async Task Migrate_CreatesBackupBeforeUpgrading()
    {
        using var fixture = new TestDatabase();
        var runner = fixture.Runner();
        await runner.MigrateAsync();

        var outcome = await runner.MigrateAsync();

        Assert.True(Directory.Exists(fixture.Backups));
    }

    [Fact]
    public async Task FailingMigration_RollsBackAndKeepsVersion()
    {
        using var fixture = new TestDatabase();
        var broken = new List<Migration>
        {
            MigrationCatalog.All[0],
            new(2, "broken", "CREATE TABLE Oops (Id INTEGER); INSERT INTO Missing VALUES (1);", "DROP TABLE Oops;")
        };

        await fixture.Runner(new[] { MigrationCatalog.All[0] }).MigrateAsync();
        var runner = fixture.Runner(broken);

        await Assert.ThrowsAsync<MigrationFailedException>(() => runner.MigrateAsync());
        Assert.Equal(1, await runner.GetCurrentVersionAsync());

        await using var connection = await fixture.Database.OpenAsync();
        await using var check = connection.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='Oops'";
        Assert.Equal(0L, Convert.ToInt64(await check.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task BackupFile_IsWrittenToBackupDirectory()
    {
        using var fixture = new TestDatabase();
        await fixture.Runner(new[] { MigrationCatalog.All[0] }).MigrateAsync();

        var custom = new List<Migration>
        {
            MigrationCatalog.All[0],
            new(2, "extra", "CREATE TABLE Extra (Id INTEGER);", "DROP TABLE Extra;")
        };

        var outcome = await fixture.Runner(custom).MigrateAsync();

        Assert.NotNull(outcome.BackupPath);
        Assert.True(File.Exists(outcome.BackupPath));
    }
}

public class SessionRepositoryTests
{
    [Fact]
    public async Task InsertAndRead_RoundtripsAllFields()
    {
        using var fixture = new TestDatabase();
        await fixture.Runner().MigrateAsync();
        var repository = new SessionRepository(fixture.Database);
        var started = new DateTimeOffset(2026, 10, 31, 18, 0, 0, TimeSpan.Zero);

        long id = await repository.InsertAsync(new NewSession(
            "Cyberpunk 2077", started, started.AddMinutes(30), 108_000,
            MeanFps: 61.4, MedianFps: 63.2, P1LowFps: 42.1, P01LowFps: 33.7,
            StutterPerMinute: 2.5, StabilityIndex: 87.3, HardwareJson: "{\"gpu\":\"B580\"}"));

        var session = await repository.GetAsync(id);

        Assert.NotNull(session);
        Assert.Equal("Cyberpunk 2077", session!.GameName);
        Assert.Equal(started, session.StartedUtc);
        Assert.Equal(63.2, session.MedianFps, 3);
        Assert.Equal(87.3, session.StabilityIndex, 3);
    }

    [Fact]
    public async Task AddSamples_StoresAllRowsInOneBatch()
    {
        using var fixture = new TestDatabase();
        await fixture.Runner().MigrateAsync();
        var repository = new SessionRepository(fixture.Database);
        long id = await repository.InsertAsync(new NewSession(
            "Test", DateTimeOffset.UtcNow, null, 3, 60, 60, 60, 60, 0, 100));

        var samples = Enumerable.Range(0, 3)
            .Select(i => new FpsSampleRow(id, DateTimeOffset.UtcNow.AddSeconds(i), 60, 16.6))
            .ToList();

        await repository.AddSamplesAsync(samples);

        await using var connection = await fixture.Database.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM FpsSamples WHERE SessionId = $id";
        command.Parameters.AddWithValue("$id", id);
        Assert.Equal(3L, Convert.ToInt64(await command.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task AggregateByGame_GroupsInSql()
    {
        using var fixture = new TestDatabase();
        await fixture.Runner().MigrateAsync();
        var repository = new SessionRepository(fixture.Database);

        await repository.InsertAsync(new NewSession("A", DateTimeOffset.UtcNow, null, 100, 60, 60, 50, 40, 1, 90));
        await repository.InsertAsync(new NewSession("A", DateTimeOffset.UtcNow, null, 100, 80, 80, 70, 60, 0, 95));
        await repository.InsertAsync(new NewSession("B", DateTimeOffset.UtcNow, null, 100, 40, 40, 30, 20, 5, 70));

        var aggregates = await repository.AggregateByGameAsync();

        Assert.Equal(2, aggregates.Count);
        var a = aggregates.Single(x => x.GameName == "A");
        Assert.Equal(2, a.Sessions);
        Assert.Equal(70, a.AvgMedianFps, 3);
    }

    [Fact]
    public async Task List_FiltersByGame()
    {
        using var fixture = new TestDatabase();
        await fixture.Runner().MigrateAsync();
        var repository = new SessionRepository(fixture.Database);
        await repository.InsertAsync(new NewSession("A", DateTimeOffset.UtcNow, null, 1, 60, 60, 60, 60, 0, 100));

        Assert.Single(await repository.ListAsync("A"));
        Assert.Empty(await repository.ListAsync("B"));
    }
}

public class SnapshotStoreTests
{
    [Fact]
    public async Task SaveAndLoad_KeepsChecksumValid()
    {
        using var fixture = new TestDatabase();
        await fixture.Runner().MigrateAsync();
        var store = new SnapshotStore(fixture.Database);

        var document = new SnapshotDocument(
            SnapshotDocument.CurrentSchemaVersion, Guid.NewGuid(), DateTimeOffset.UtcNow, "Test",
            new[] { new SnapshotEntry(SnapshotTargetKind.RegistryValue, @"HKCU\X", "v", "1", true) });

        await store.SaveAsync(document);
        var loaded = await store.GetAsync(document.Id);

        Assert.NotNull(loaded);
        Assert.Equal(document.ComputeChecksum(), loaded!.ComputeChecksum());
    }

    [Fact]
    public async Task TamperedPayload_IsNotReturned()
    {
        using var fixture = new TestDatabase();
        await fixture.Runner().MigrateAsync();
        var store = new SnapshotStore(fixture.Database);

        var document = new SnapshotDocument(
            SnapshotDocument.CurrentSchemaVersion, Guid.NewGuid(), DateTimeOffset.UtcNow, "Test",
            new[] { new SnapshotEntry(SnapshotTargetKind.RegistryValue, @"HKCU\X", "v", "1", true) });
        await store.SaveAsync(document);

        await using (var connection = await fixture.Database.OpenAsync())
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE Snapshots SET Payload = replace(Payload, '\"1\"', '\"0\"') WHERE Id = $id";
            command.Parameters.AddWithValue("$id", document.Id.ToString("N"));
            await command.ExecuteNonQueryAsync();
        }

        Assert.Null(await store.GetAsync(document.Id));
    }

    [Fact]
    public async Task OpenSnapshots_ExcludeRolledBackOnes()
    {
        using var fixture = new TestDatabase();
        await fixture.Runner().MigrateAsync();
        var store = new SnapshotStore(fixture.Database);

        var first = new SnapshotDocument(SnapshotDocument.CurrentSchemaVersion, Guid.NewGuid(), DateTimeOffset.UtcNow, "1", Array.Empty<SnapshotEntry>());
        var second = new SnapshotDocument(SnapshotDocument.CurrentSchemaVersion, Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(1), "2", Array.Empty<SnapshotEntry>());

        await store.SaveAsync(first);
        await store.SaveAsync(second);
        await store.MarkRolledBackAsync(first.Id);

        var open = await store.GetOpenAsync();

        Assert.Single(open);
        Assert.Equal(second.Id, open[0].Id);
        Assert.Equal(second.Id, (await store.GetLatestAsync())!.Id);
    }
}

public class RetentionServiceTests
{
    [Fact]
    public async Task Summarize_MovesOldSamplesIntoBucketsAndDeletesRaw()
    {
        using var fixture = new TestDatabase();
        await fixture.Runner().MigrateAsync();
        var repository = new SessionRepository(fixture.Database);
        long id = await repository.InsertAsync(new NewSession("Test", DateTimeOffset.UtcNow.AddDays(-60), null, 4, 60, 60, 60, 60, 0, 90));

        var old = DateTimeOffset.UtcNow.AddDays(-45);
        await repository.AddSamplesAsync(new[]
        {
            new FpsSampleRow(id, old, 60, 16.6),
            new FpsSampleRow(id, old.AddSeconds(1), 70, 14.2),
            new FpsSampleRow(id, DateTimeOffset.UtcNow, 80, 12.5)
        });

        var result = await new RetentionService(fixture.Database).SummarizeAsync(rawRetentionDays: 30);

        Assert.Equal(1, result.SummariesCreated);
        Assert.Equal(2, result.RawSamplesDeleted);

        await using var connection = await fixture.Database.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Samples FROM SampleSummaries";
        Assert.Equal(2L, Convert.ToInt64(await command.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task Summarize_KeepsRecentSamples()
    {
        using var fixture = new TestDatabase();
        await fixture.Runner().MigrateAsync();
        var repository = new SessionRepository(fixture.Database);
        long id = await repository.InsertAsync(new NewSession("Test", DateTimeOffset.UtcNow, null, 1, 60, 60, 60, 60, 0, 90));
        await repository.AddSamplesAsync(new[] { new FpsSampleRow(id, DateTimeOffset.UtcNow, 60, 16.6) });

        var result = await new RetentionService(fixture.Database).SummarizeAsync(rawRetentionDays: 30);

        Assert.Equal(0, result.RawSamplesDeleted);
    }
}
