using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NexusOptimizer.Core.Snapshots;

public enum SnapshotTargetKind
{
    RegistryValue,
    ServiceStartType,
    PowerPlan,
    ApplicationSetting
}

/// <summary>Ein einzelner gesicherter Zustand. PreviousValue = null bedeutet "existierte nicht".</summary>
public sealed record SnapshotEntry(
    SnapshotTargetKind Kind,
    string Path,
    string? Name,
    string? PreviousValue,
    bool Existed);

/// <summary>
/// Vollständiger Snapshot aller von einem Plan betroffenen Zustände.
/// Die Prüfsumme deckt den kompletten Inhalt ab, damit ein manipulierter oder
/// abgeschnittener Snapshot beim Rollback erkannt wird.
/// </summary>
public sealed record SnapshotDocument(
    int SchemaVersion,
    Guid Id,
    DateTimeOffset CreatedUtc,
    string Description,
    IReadOnlyList<SnapshotEntry> Entries)
{
    public const int CurrentSchemaVersion = 1;

    public string ComputeChecksum()
    {
        var builder = new StringBuilder();
        builder.Append(SchemaVersion).Append('|')
               .Append(Id.ToString("N")).Append('|')
               .Append(CreatedUtc.ToUnixTimeSeconds()).Append('|')
               .Append(Description).Append('|');

        foreach (var entry in Entries)
        {
            builder.Append(entry.Kind).Append(':')
                   .Append(entry.Path).Append(':')
                   .Append(entry.Name ?? "-").Append(':')
                   .Append(entry.PreviousValue ?? "-").Append(':')
                   .Append(entry.Existed ? '1' : '0').Append(';');
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(hash);
    }
}

public sealed record SnapshotEnvelope(string Checksum, SnapshotDocument Document);

public static class SnapshotCodec
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() }
    };

    public static SnapshotEnvelope Wrap(SnapshotDocument document) =>
        new(document.ComputeChecksum(), document);

    public static string Serialize(SnapshotDocument document) =>
        JsonSerializer.Serialize(Wrap(document), Options);

    public static Result<SnapshotDocument> Deserialize(string json)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize<SnapshotEnvelope>(json, Options);
            if (envelope is null) return Result<SnapshotDocument>.Fail("Snapshot leer");

            if (envelope.Document.SchemaVersion != SnapshotDocument.CurrentSchemaVersion)
                return Result<SnapshotDocument>.Fail(
                    $"Unbekannte Schema-Version {envelope.Document.SchemaVersion}");

            string actual = envelope.Document.ComputeChecksum();
            if (!string.Equals(actual, envelope.Checksum, StringComparison.OrdinalIgnoreCase))
                return Result<SnapshotDocument>.Fail("Prüfsumme stimmt nicht - Snapshot beschädigt");

            return Result<SnapshotDocument>.Ok(envelope.Document);
        }
        catch (JsonException ex)
        {
            return Result<SnapshotDocument>.Fail("Snapshot nicht lesbar: " + ex.Message);
        }
    }
}
