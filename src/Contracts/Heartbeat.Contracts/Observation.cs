namespace Heartbeat.Contracts;

public sealed class Observation : IEntity, ITimed
{
    public required EntityId Id { get; init; }
    public required EntityId ObserverId { get; init; }
    public required EntityId DataId { get; init; }
    public required EntityId SchemaId { get; init; }
    public DateTimeOffset? StartAt { get; init; }
    public DateTimeOffset? EndAt { get; init; }
    public string? TimeZone { get; init; }
}
