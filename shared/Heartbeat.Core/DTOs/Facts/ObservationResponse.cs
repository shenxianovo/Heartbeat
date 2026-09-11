using System.Text.Json;

namespace Heartbeat.Core.DTOs.Facts;

public sealed record ObjectSummary(Guid Id, string Kind, string Scope, string Key, string? Name);
public sealed record RelationMemberResponse(string Role, ObjectSummary Object);
public sealed record RelationResponse(Guid Id, string Kind, DateTimeOffset? ValidFrom, DateTimeOffset? ValidTo,
    JsonElement Evidence, IReadOnlyList<RelationMemberResponse> Members);

public abstract class ObservationResponse
{
    public Guid Id { get; set; }
    public Guid? CollectorId { get; set; }
    public Guid? FoiId { get; set; }
    public ObjectSummary? Foi { get; set; }
    public IReadOnlyList<RelationResponse> Relations { get; set; } = [];
    // Read alias for clients predating the direct object contract.
    public Guid? ObserverId { get => CollectorId; set => CollectorId = value; }
}
