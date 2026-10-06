using System.Text.Json;
using Heartbeat.Core;

namespace Heartbeat.Infrastructure.Database;

public sealed class EntityDataRow
{
    public required EntityId Id { get; init; }
    public required JsonElement Data { get; init; }
}
