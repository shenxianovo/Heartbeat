namespace Heartbeat.Contracts;

public abstract class Relation : IEntity, ITimed
{
    public required EntityId Id { get; init; }
    public required EntityId FromId { get; init; }
    public required EntityId ToId { get; init; }
    public DateTimeOffset? StartAt { get; init; }
    public DateTimeOffset? EndAt { get; init; }
}
