namespace NexusOptimizer.Infrastructure.Migrations;

public sealed record Migration(int Version, string Name, string UpSql, string DownSql);

/// <summary>
/// Versioniertes Schema. Jede Migration hat einen Up- und einen Down-Pfad; die
/// Version steht in einer eigenen Tabelle. Regeln:
///  - nur additiv ändern (Spalten/Tabellen/Indizes), kein Datenverlust in Up
///  - Down ist die exakte Rücknahme und wird nur für Tests/Rollback-Werkzeuge genutzt
/// </summary>
public static class MigrationCatalog
{
    public static IReadOnlyList<Migration> All { get; } = new List<Migration>
    {
        new(
            1, "initial-schema",
            """
            CREATE TABLE IF NOT EXISTS Sessions (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                GameName TEXT NOT NULL,
                StartedUtc TEXT NOT NULL,
                EndedUtc TEXT,
                Frames INTEGER NOT NULL,
                MeanFps REAL NOT NULL,
                MedianFps REAL NOT NULL,
                P1LowFps REAL NOT NULL,
                P01LowFps REAL NOT NULL,
                StutterPerMinute REAL NOT NULL,
                StabilityIndex REAL NOT NULL,
                HardwareJson TEXT
            );
            CREATE INDEX IF NOT EXISTS IX_Sessions_Game_Start ON Sessions(GameName, StartedUtc);

            CREATE TABLE IF NOT EXISTS FpsSamples (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                SessionId INTEGER NOT NULL REFERENCES Sessions(Id) ON DELETE CASCADE,
                TimestampUtc TEXT NOT NULL,
                Fps REAL NOT NULL,
                FrametimeMs REAL NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_FpsSamples_Session ON FpsSamples(SessionId, TimestampUtc);

            CREATE TABLE IF NOT EXISTS SampleSummaries (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                SessionId INTEGER NOT NULL,
                BucketUtc TEXT NOT NULL,
                Samples INTEGER NOT NULL,
                AvgFps REAL NOT NULL,
                P99FrametimeMs REAL NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_SampleSummaries_Session ON SampleSummaries(SessionId, BucketUtc);

            CREATE TABLE IF NOT EXISTS Snapshots (
                Id TEXT PRIMARY KEY,
                CreatedUtc TEXT NOT NULL,
                Checksum TEXT NOT NULL,
                Payload TEXT NOT NULL,
                RolledBack INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS Settings (
                Key TEXT PRIMARY KEY,
                Value TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS OptimizationLog (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                SnapshotId TEXT,
                ActionId TEXT NOT NULL,
                Enabled INTEGER NOT NULL,
                Result TEXT NOT NULL,
                CreatedUtc TEXT NOT NULL
            );
            """,
            """
            DROP TABLE IF EXISTS OptimizationLog;
            DROP TABLE IF EXISTS Settings;
            DROP TABLE IF EXISTS Snapshots;
            DROP TABLE IF EXISTS SampleSummaries;
            DROP TABLE IF EXISTS FpsSamples;
            DROP TABLE IF EXISTS Sessions;
            """),

        new(
            2, "settings-timestamps",
            """
            ALTER TABLE Settings ADD COLUMN UpdatedUtc TEXT;
            """,
            """
            ALTER TABLE Settings DROP COLUMN UpdatedUtc;
            """)
    };
}
