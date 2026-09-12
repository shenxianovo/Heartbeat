using System.Text.Json;
using System.Text.Json.Serialization;

namespace Heartbeat.Hub;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CollectorDeclaration(string? Key, string? Target, string? DisplayName);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TrackDeclaration(
    string? Type,
    int Version,
    string? TimeMode,
    string? EndMode);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RecordSnapshot(
    Guid Id,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt,
    DateTimeOffset? ObservedAt,
    JsonElement Value);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HubSubmission(
    CollectorDeclaration? Collector,
    TrackDeclaration? Track,
    IReadOnlyList<RecordSnapshot?>? Records);

public sealed record HubSubmissionReceipt(int? Index, Guid? Id, string? Status, DateTimeOffset? EndedAt);

public sealed record HubSubmissionResponse(IReadOnlyList<HubSubmissionReceipt?>? Results);
