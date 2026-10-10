namespace Heartbeat.Core;

public sealed class Observation : IEntity, ITimed
{
    public required EntityId Id { get; init; }
    public required EntityId ObserverId { get; init; }
    public required EntityId ContentId { get; init; }
    public required EntityId SchemaId { get; init; }
    public DateTimeOffset? StartAt { get; init; }
    public DateTimeOffset? EndAt { get; init; }
    public string? TimeZone { get; init; }

    public IEnumerable<EntityId> GetReferences() => [ObserverId, ContentId, SchemaId];
}
