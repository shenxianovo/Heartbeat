using Heartbeat.Core;

namespace Heartbeat.Observers.ForegroundState;

internal sealed class ForegroundApplicationContent
{
    public required EntityId Id { get; init; }
    public required EntityId LocalApplicationId { get; init; }
    public required string? Name { get; init; }
}
