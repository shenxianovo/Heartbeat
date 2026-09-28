namespace Heartbeat.Collector.VRChat;

internal sealed record VRChatLocation(string WorldId, string InstanceId)
{
    public static VRChatLocation? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var separator = value.IndexOf(':');
        if (separator < 0) return null;
        var world = value[..separator];
        var instance = value[(separator + 1)..];
        return world.StartsWith("wrld_", StringComparison.Ordinal) && instance.Length > 0
            ? new(world, instance) : null;
    }
}

internal sealed record VRChatPresenceUpdate(string AccountId, string? DisplayName, VRChatLocation? Location,
    DateTimeOffset At, string Cause);

internal sealed record VRChatPresenceSnapshot(DateTimeOffset RequestedAt,
    IReadOnlyList<VRChatPresenceUpdate> Users);

internal interface IVRChatEventConnection : IDisposable
{
    Task<VRChatPresenceUpdate?> ReadAsync(CancellationToken token);
}
