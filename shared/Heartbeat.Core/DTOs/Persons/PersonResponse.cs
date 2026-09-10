using Heartbeat.Core.DTOs.Facts;

namespace Heartbeat.Core.DTOs.Persons;

public sealed record PersonResponse(long Id, Guid Reference);
[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record PersonAssociationRequest(long? DeviceId, long? AccountId,
    [property: System.Text.Json.Serialization.JsonRequired] DateTimeOffset? Start,
    [property: System.Text.Json.Serialization.JsonRequired] DateTimeOffset? End);
public sealed record PersonAssociationResponse(long Id, long? DeviceId, long? AccountId, DateTimeOffset? Start, DateTimeOffset? End);
public sealed record EffectiveInterval(DateTimeOffset Start, DateTimeOffset End);
public sealed record PersonFactItem(FactResponse Fact, IReadOnlyList<EffectiveInterval> EffectiveIntervals, double? EffectiveSeconds, string TargetName);
public sealed record PersonSourceCount(string Source, int Count);
public sealed record PersonFactPage(IReadOnlyList<PersonFactItem> Items, int TotalCount, IReadOnlyList<PersonSourceCount> Sources);

public sealed record PersonTargetOption(string Kind, long Id, string Name);
public sealed record PersonSettingsResponse(PersonResponse? Person, IReadOnlyList<PersonTargetOption> Targets, IReadOnlyList<PersonAssociationResponse> Associations);
