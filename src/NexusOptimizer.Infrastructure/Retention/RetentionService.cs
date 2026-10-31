using System.Globalization;

namespace NexusOptimizer.Infrastructure.Retention;

public sealed record RetentionResult(int SummariesCreated, int RawSamplesDeleted, DateTimeOffset CutoffUtc);

/// <summary>
/// Verdichtet alte Roh-Samples zu Minutensummaries und löscht anschließend die Rohdaten.
/// Läuft in einer Transaktion, damit ein Abbruch keinen halben Zustand hinterlässt.
/// </summary>
public sealed class RetentionService
{
    private readonly SqliteDatabase _database;

    public RetentionService(SqliteDatabase database) => _database = database;

    public async Task<RetentionResult> SummarizeAsync(
        int rawRetentionDays = 30,
        int bucketMinutes = 1,
        CancellationToken cancellationToken = default)
    {
        if (rawRetentionDays < 1) throw new ArgumentOutOfRangeException(nameof(rawRetentionDays));
        if (bucketMinutes is < 1 or > 60) throw new ArgumentOutOfRangeException(nameof(bucketMinutes));

        var cutoff = DateTimeOffset.UtcNow.AddDays(-rawRetentionDays);
        string cutoffText = cutoff.ToString("O", CultureInfo.InvariantCulture);

        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (Microsoft.Data.Sqlite.SqliteTransaction)
            await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        int summaries;
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            // Bucket = Zeitstempel auf Minuten (bzw. bucketMinutes) abgeschnitten.
            insert.CommandText = $"""
                INSERT INTO SampleSummaries (SessionId, BucketUtc, Samples, AvgFps, P99FrametimeMs)
                SELECT SessionId,
                       strftime('%Y-%m-%dT%H:%M:00Z', TimestampUtc, '-{(bucketMinutes - 1)} minutes') AS Bucket,
                       COUNT(*),
                       AVG(Fps),
                       MAX(FrametimeMs)
                FROM FpsSamples
                WHERE TimestampUtc < $cutoff
                GROUP BY SessionId, Bucket;
                """;
            insert.Parameters.AddWithValue("$cutoff", cutoffText);
            summaries = await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        int deleted;
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM FpsSamples WHERE TimestampUtc < $cutoff";
            delete.Parameters.AddWithValue("$cutoff", cutoffText);
            deleted = await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new RetentionResult(summaries, deleted, cutoff);
    }
}
