using Heartbeat.Core;

namespace Heartbeat.Api.Entities;

public sealed class SaveObservationRequest
{
    public required EntityId ObserverId { get; init; }
    public required EntityId DataId { get; init; }
    public required EntityId SchemaId { get; init; }
    public required DateTimeOffset? StartAt { get; init; }
    public required DateTimeOffset? EndAt { get; init; }
    public required string? TimeZone { get; init; }
}
