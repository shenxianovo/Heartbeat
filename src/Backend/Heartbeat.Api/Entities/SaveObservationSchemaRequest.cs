using System.Text.Json;

namespace Heartbeat.Api.Entities;

public sealed class SaveObservationSchemaRequest
{
    public required string Name { get; init; }
    public required JsonElement Schema { get; init; }
    public required DateTimeOffset? StartAt { get; init; }
    public required DateTimeOffset? EndAt { get; init; }
}
