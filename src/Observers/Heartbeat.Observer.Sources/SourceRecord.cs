using System.Text.Json;

namespace Heartbeat.Observers.Sources;

/// <summary>A fixed, source-specific contract. IDs and resources are shared across collector instances.</summary>
public sealed record SourceSchema(Guid Id, string ResourceName, string Name, JsonElement Fields);

/// <summary>A known source identity, not a claim that its referenced content has been collected.</summary>
public sealed record SourceReference(string Kind, string NativeKey);

/// <summary>
/// A source-native interpretation. Same scope/evidence key is replay; independent observations
/// require a new evidence key, even when their content is unchanged. Native keys remain source data.
/// </summary>
public sealed record SourceRecord(
    string Kind,
    string NativeKey,
    string EvidenceKey,
    SourceSchema Schema,
    JsonElement Properties,
    IReadOnlyDictionary<string, SourceReference> References,
    DateTimeOffset? StartAt = null,
    DateTimeOffset? EndAt = null,
    string? TimeZone = null);

public sealed record SourceReadContext(string Input, string Scope, string CaptureKey, DateTimeOffset? CapturedAt = null);

public interface IRecordSource
{
    string Name { get; }
    IAsyncEnumerable<SourceRecord> ReadAsync(SourceReadContext context, CancellationToken cancellationToken);
}
