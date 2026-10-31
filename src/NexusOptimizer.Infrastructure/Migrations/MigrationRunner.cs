using System.Globalization;
using Microsoft.Data.Sqlite;
using Serilog;

namespace NexusOptimizer.Infrastructure.Migrations;

public sealed record MigrationOutcome(int FromVersion, int ToVersion, string? BackupPath, IReadOnlyList<string> Applied);

/// <summary>
/// Führt Migrationen in einer Transaktion aus, nachdem ein Backup der Datei erstellt
/// wurde. Schlägt eine Migration fehl, wird die Transaktion zurückgerollt und der
/// Runner meldet die Ursache - der Aufrufer startet dann im sicheren Modus.
/// </summary>
public sealed class MigrationRunner
{
    private readonly SqliteDatabase _database;
    private readonly string _backupDirectory;
    private readonly IReadOnlyList<Migration> _migrations;

    public MigrationRunner(SqliteDatabase database, string backupDirectory, IReadOnlyList<Migration>? migrations = null)
    {
        _database = database;
        _backupDirectory = backupDirectory;
        _migrations = (migrations ?? MigrationCatalog.All)
            .OrderBy(m => m.Version)
            .ToArray();
    }

    public async Task<int> GetCurrentVersionAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await EnsureVersionTableAsync(connection, cancellationToken).ConfigureAwait(false);
        return await ReadVersionAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    public async Task<MigrationOutcome> MigrateAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await EnsureVersionTableAsync(connection, cancellationToken).ConfigureAwait(false);

        int current = await ReadVersionAsync(connection, cancellationToken).ConfigureAwait(false);
        int target = _migrations.Count == 0 ? current : _migrations.Max(m => m.Version);
        var applied = new List<string>();

        if (current >= target)
            return new MigrationOutcome(current, current, null, applied);

        string? backup = null;
        if (File.Exists(_database.DatabasePath) && new FileInfo(_database.DatabasePath).Length > 0)
        {
            Directory.CreateDirectory(_backupDirectory);
            backup = Path.Combine(_backupDirectory,
                $"nexus-v{current}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.db");
            _database.BackupTo(backup);
        }

        foreach (var migration in _migrations.Where(m => m.Version > current))
        {
            using var transaction = connection.BeginTransaction();
            try
            {
                await using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = migration.UpSql;
                    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                await using (var update = connection.CreateCommand())
                {
                    update.Transaction = transaction;
                    update.CommandText = "UPDATE SchemaVersion SET Version = $v";
                    update.Parameters.AddWithValue("$v", migration.Version);
                    await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                transaction.Commit();
                applied.Add($"{migration.Version}:{migration.Name}");
                Log.Information("Migration {Version} ({Name}) angewendet", migration.Version, migration.Name);
            }
            catch (Exception ex)
            {
                transaction.Rollback();
                Log.Error(ex, "Migration {Version} fehlgeschlagen, Rollback erfolgt", migration.Version);
                throw new MigrationFailedException(migration.Version, migration.Name, backup, ex);
            }
        }

        return new MigrationOutcome(current, target, backup, applied);
    }

    private static async Task EnsureVersionTableAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS SchemaVersion (Version INTEGER NOT NULL);
            INSERT INTO SchemaVersion(Version) SELECT 0 WHERE NOT EXISTS (SELECT 1 FROM SchemaVersion);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> ReadVersionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT MAX(Version) FROM SchemaVersion";
        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is null or DBNull ? 0 : Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }
}

public sealed class MigrationFailedException : Exception
{
    public MigrationFailedException(int version, string name, string? backupPath, Exception inner)
        : base($"Migration {version} ({name}) fehlgeschlagen. Backup: {backupPath ?? "keins"}", inner)
    {
        Version = version;
        Name = name;
        BackupPath = backupPath;
    }

    public int Version { get; }
    public string Name { get; }
    public string? BackupPath { get; }
}
