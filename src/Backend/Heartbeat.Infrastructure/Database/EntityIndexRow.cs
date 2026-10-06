using Heartbeat.Core;

namespace Heartbeat.Infrastructure.Database;

public sealed class EntityIndexRow
{
    public required EntityId Id { get; init; }
    public required string TableName { get; init; }
}
