using System.Text.Json;
using Heartbeat.Hub;

namespace Heartbeat.Collector.VRChat;

internal sealed record VRChatPresence(string WorldId, string? WorldName, string InstanceId, string? ObservedAccountId);

// Retains main's account/world/instance continuity rule, using the current monotonic Record contract.
internal sealed class PresenceRecords(string target, TimeSpan maximumGap)
{
    private VRChatPresence? _presence;
    private RecordSnapshot? _current;

    public RecordSnapshot? Observe(VRChatPresence? presence, DateTimeOffset at)
    {
        if (presence is null) { Break(); return null; }
        if (presence.ObservedAccountId != target) throw new InvalidDataException("VRChat returned a different account.");
        if (_current is not null && at < _current.EndedAt) { Break(); return null; }
        _current = Continues(presence, at) ? _current! with { EndedAt = at } : new RecordSnapshot(
            Guid.CreateVersion7(at), at, at, null,
            JsonSerializer.SerializeToElement(new { account_id = target, world_id = presence.WorldId,
                world_name = presence.WorldName, instance_id = presence.InstanceId }));
        _presence = presence;
        return _current;
    }

    private bool Continues(VRChatPresence presence, DateTimeOffset at) =>
        _current is not null && at - _current.EndedAt <= maximumGap &&
        _presence?.WorldId == presence.WorldId && _presence?.InstanceId == presence.InstanceId;

    public void Break() { _presence = null; _current = null; }
}
