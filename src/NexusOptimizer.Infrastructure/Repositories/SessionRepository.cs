using System.Globalization;
using Dapper;
using Microsoft.Data.Sqlite;

namespace NexusOptimizer.Infrastructure.Repositories;

public sealed record SessionRecord(
    long Id,
    string GameName,
    DateTimeOffset StartedUtc,
    DateTimeOffset? EndedUtc,
    long Frames,
    double MeanFps,
    double MedianFps,
    double P1LowFps,
    double P01LowFps,
    double StutterPerMinute,
    double StabilityIndex,
    string? HardwareJson = null);

public sealed record NewSession(
    string GameName,
    DateTimeOffset StartedUtc,
    DateTimeOffset? EndedUtc,
    long Frames,
    double MeanFps,
    double MedianFps,
    double P1LowFps,
    double P01LowFps,
    double StutterPerMinute,
    double StabilityIndex,
    string? HardwareJson = null);

public sealed record FpsSampleRow(long SessionId, DateTimeOffset TimestampUtc, double Fps, double FrametimeMs);

public sealed record GameAggregate(string GameName, long Sessions, double AvgMedianFps, double AvgStabilityIndex);

public sealed class SessionRepository
{
    private readonly SqliteDatabase _database;

    public SessionRepository(SqliteDatabase database) => _database = database;

    public async Task<long> InsertAsync(NewSession session, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Sessions (GameName, StartedUtc, EndedUtc, Frames, MeanFps, MedianFps,
                                  P1LowFps, P01LowFps, StutterPerMinute, StabilityIndex, HardwareJson)
            VALUES ($game, $start, $end, $frames, $mean, $median, $p1, $p01, $stutter, $stability, $hardware);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$game", session.GameName);
        command.Parameters.AddWithValue("$start", session.StartedUtc.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$end", session.EndedUtc?.ToString("O", CultureInfo.InvariantCulture) ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$frames", session.Frames);
        command.Parameters.AddWithValue("$mean", session.MeanFps);
        command.Parameters.AddWithValue("$median", session.MedianFps);
        command.Parameters.AddWithValue("$p1", session.P1LowFps);
        command.Parameters.AddWithValue("$p01", session.P01LowFps);
        command.Parameters.AddWithValue("$stutter", session.StutterPerMinute);
        command.Parameters.AddWithValue("$stability", session.StabilityIndex);
        command.Parameters.AddWithValue("$hardware", session.HardwareJson ?? (object)DBNull.Value);

        object? id = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(id, CultureInfo.InvariantCulture);
    }

    /// <summary>Schreibt Samples gebündelt in einer Transaktion (max. ein Commit pro Batch).</summary>
    public async Task AddSamplesAsync(IReadOnlyCollection<FpsSampleRow> samples, CancellationToken cancellationToken = default)
    {
        if (samples.Count == 0) return;

        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO FpsSamples (SessionId, TimestampUtc, Fps, FrametimeMs)
            VALUES ($session, $ts, $fps, $frametime);
            """;

        var sessionParam = command.Parameters.Add("$session", SqliteType.Integer);
        var tsParam = command.Parameters.Add("$ts", SqliteType.Text);
        var fpsParam = command.Parameters.Add("$fps", SqliteType.Real);
        var ftParam = command.Parameters.Add("$frametime", SqliteType.Real);

        foreach (var sample in samples)
        {
            sessionParam.Value = sample.SessionId;
            tsParam.Value = sample.TimestampUtc.ToString("O", CultureInfo.InvariantCulture);
            fpsParam.Value = sample.Fps;
            ftParam.Value = sample.FrametimeMs;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<SessionRecord?> GetAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Sessions WHERE Id = $id";
        command.Parameters.AddWithValue("$id", id);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
    }

    public async Task<IReadOnlyList<SessionRecord>> ListAsync(string? gameName = null, int limit = 100, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = gameName is null
            ? "SELECT * FROM Sessions ORDER BY StartedUtc DESC LIMIT $limit"
            : "SELECT * FROM Sessions WHERE GameName = $game ORDER BY StartedUtc DESC LIMIT $limit";
        command.Parameters.AddWithValue("$limit", limit);
        if (gameName is not null) command.Parameters.AddWithValue("$game", gameName);

        var result = new List<SessionRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(Map(reader));

        return result;
    }

    /// <summary>Aggregation per SQL, nicht im Speicher.</summary>
    public async Task<IReadOnlyList<GameAggregate>> AggregateByGameAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<GameAggregate>(new CommandDefinition("""
            SELECT GameName,
                   COUNT(*) AS Sessions,
                   AVG(MedianFps) AS AvgMedianFps,
                   AVG(StabilityIndex) AS AvgStabilityIndex
            FROM Sessions
            GROUP BY GameName
            ORDER BY Sessions DESC, GameName
            """, cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.AsList();
    }

    private static SessionRecord Map(SqliteDataReader reader) => new(
        Id: reader.GetInt64(0),
        GameName: reader.GetString(1),
        StartedUtc: ParseDate(reader.GetString(2)),
        EndedUtc: reader.IsDBNull(3) ? null : ParseDate(reader.GetString(3)),
        Frames: reader.GetInt64(4),
        MeanFps: reader.GetDouble(5),
        MedianFps: reader.GetDouble(6),
        P1LowFps: reader.GetDouble(7),
        P01LowFps: reader.GetDouble(8),
        StutterPerMinute: reader.GetDouble(9),
        StabilityIndex: reader.GetDouble(10),
        HardwareJson: reader.IsDBNull(11) ? null : reader.GetString(11));

    private static DateTimeOffset ParseDate(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
