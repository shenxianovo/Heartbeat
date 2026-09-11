using System.Text.Json;

namespace Heartbeat.Server.Entities;

public sealed class ObservationCollector
{
    public Guid Id { get; set; }
    public string OwnerId { get; set; } = string.Empty;
    public string? Kind { get; set; }
    public string? Name { get; set; }
}

public sealed class ObservationObject
{
    public Guid Id { get; set; }
    public string? OwnerId { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string Scope { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string? Name { get; set; }
}

public sealed class ObjectRelation
{
    public Guid Id { get; set; }
    public string OwnerId { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public DateTimeOffset? ValidFrom { get; set; }
    public DateTimeOffset? ValidTo { get; set; }
    public JsonDocument Evidence { get; set; } = null!;
    // Derived from Evidence, so indexing and real foreign keys do not create a second authority.
    public Guid? FactId { get; set; }
    public List<RelationMember> Members { get; set; } = [];
}

public sealed class RelationMember
{
    public Guid RelationId { get; set; }
    public string Role { get; set; } = string.Empty;
    public Guid ObjectId { get; set; }
    public ObjectRelation Relation { get; set; } = null!;
    public ObservationObject Object { get; set; } = null!;
}
