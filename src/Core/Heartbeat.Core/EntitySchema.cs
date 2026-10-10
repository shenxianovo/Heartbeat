using System.Text.Json;

namespace Heartbeat.Core;

public sealed class EntitySchema : IEntity
{
    public required EntityId Id { get; init; }
    public required string Name { get; init; }
    public required string ResourceName { get; init; }
    public required JsonElement Fields { get; init; }

    public IEnumerable<EntityId> GetReferences() => [];
}
