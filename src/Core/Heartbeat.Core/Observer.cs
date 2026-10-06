namespace Heartbeat.Core;

public sealed class Observer : IEntity
{
    public required EntityId Id { get; init; }
    public required string Name { get; init; }
}
