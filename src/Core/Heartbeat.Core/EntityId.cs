namespace Heartbeat.Core;

public readonly record struct EntityId(Guid Value)
{
    public static EntityId New() => new(Guid.CreateVersion7());
}
