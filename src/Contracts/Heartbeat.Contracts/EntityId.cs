namespace Heartbeat.Contracts;

public readonly record struct EntityId(Guid Value)
{
    public static EntityId New() => new(Guid.CreateVersion7());
}
