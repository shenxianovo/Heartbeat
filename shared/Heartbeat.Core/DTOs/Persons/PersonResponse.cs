using Heartbeat.Core.DTOs.Facts;

namespace Heartbeat.Core.DTOs.Persons;

public sealed record PersonResponse(Guid Id, Guid Reference);
[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record PersonAssociationRequest(Guid ObjectId,
    [property: System.Text.Json.Serialization.JsonRequired] DateTimeOffset? Start,
    [property: System.Text.Json.Serialization.JsonRequired] DateTimeOffset? End);
public sealed record PersonAssociationResponse(Guid Id, Guid ObjectId, DateTimeOffset? Start, DateTimeOffset? End);
public sealed record EffectiveInterval(DateTimeOffset Start, DateTimeOffset End);
public sealed record PersonFactItem(FactResponse Fact, IReadOnlyList<EffectiveInterval> EffectiveIntervals, double? EffectiveSeconds);
public sealed record PersonSourceCount(string? Source, int Count);
public sealed record PersonFactPage(IReadOnlyList<PersonFactItem> Items, int TotalCount, IReadOnlyList<PersonSourceCount> Sources);
public sealed record PersonSettingsResponse(PersonResponse? Person, IReadOnlyList<ObjectSummary> Objects, IReadOnlyList<PersonAssociationResponse> Associations);
