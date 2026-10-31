using System.Globalization;
using NexusOptimizer.Core.Snapshots;

namespace NexusOptimizer.Infrastructure.Repositories;

public sealed record StoredSnapshot(Guid Id, DateTimeOffset CreatedUtc, string Checksum, bool RolledBack);

/// <summary>
/// Persistiert Snapshots als JSON mit Prüfsumme. Der Codec prüft beim Laden, ob der
/// Inhalt unverändert ist; ein beschädigter Snapshot wird niemals stillschweigend
/// zum Rollback verwendet.
/// </summary>
public sealed class SnapshotStore
{
    private readonly SqliteDatabase _database;

    public SnapshotStore(SqliteDatabase database) => _database = database;

    public async Task SaveAsync(SnapshotDocument document, CancellationToken cancellationToken = default)
    {
        string payload = SnapshotCodec.Serialize(document);
        string checksum = document.ComputeChecksum();

        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Snapshots (Id, CreatedUtc, Checksum, Payload, RolledBack)
            VALUES ($id, $created, $checksum, $payload, 0)
            ON CONFLICT(Id) DO UPDATE SET Payload = $payload, Checksum = $checksum;
            """;
        command.Parameters.AddWithValue("$id", document.Id.ToString("N"));
        command.Parameters.AddWithValue("$created", document.CreatedUtc.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$checksum", checksum);
        command.Parameters.AddWithValue("$payload", payload);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<SnapshotDocument?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Payload FROM Snapshots WHERE Id = $id";
        command.Parameters.AddWithValue("$id", id.ToString("N"));

        string? payload = (string?)await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (payload is null) return null;

        var parsed = SnapshotCodec.Deserialize(payload);
        return parsed.IsSuccess ? parsed.Value : null;
    }

    public async Task<StoredSnapshot?> GetLatestAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, CreatedUtc, Checksum, RolledBack FROM Snapshots ORDER BY CreatedUtc DESC LIMIT 1";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;

        return new StoredSnapshot(
            Guid.ParseExact(reader.GetString(0), "N"),
            DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            reader.GetString(2),
            reader.GetInt64(3) != 0);
    }

    /// <summary>Snapshots, die noch nicht zurückgerollt wurden - Kandidaten für den Startdialog.</summary>
    public async Task<IReadOnlyList<StoredSnapshot>> GetOpenAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, CreatedUtc, Checksum, RolledBack FROM Snapshots WHERE RolledBack = 0 ORDER BY CreatedUtc DESC";

        var result = new List<StoredSnapshot>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new StoredSnapshot(
                Guid.ParseExact(reader.GetString(0), "N"),
                DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                reader.GetString(2),
                reader.GetInt64(3) != 0));
        }

        return result;
    }

    public async Task MarkRolledBackAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Snapshots SET RolledBack = 1 WHERE Id = $id";
        command.Parameters.AddWithValue("$id", id.ToString("N"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
