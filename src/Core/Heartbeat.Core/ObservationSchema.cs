using System.Text.Json;

namespace Heartbeat.Core;

public sealed class ObservationSchema : IEntity, ITimed
{
    public required EntityId Id { get; init; }
    public required string Name { get; init; }
    public required JsonElement Schema { get; init; }
    public DateTimeOffset? StartAt { get; init; }
    public DateTimeOffset? EndAt { get; init; }
}
