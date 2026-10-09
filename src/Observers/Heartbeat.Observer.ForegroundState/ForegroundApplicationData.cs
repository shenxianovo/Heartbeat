using Heartbeat.Core;

namespace Heartbeat.Observers.ForegroundState;

internal sealed class ForegroundApplicationData : IEntity
{
    public required EntityId Id { get; init; }
    public required string? BundleIdentifier { get; init; }
    public required string? Name { get; init; }
    public required string? ExecutablePath { get; init; }
}
