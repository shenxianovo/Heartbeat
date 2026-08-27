namespace Heartbeat.Collector.VRChat;

public sealed record VRChatPresence(
    string WorldId,
    string? WorldName,
    string InstanceId);

public sealed record VRChatPresenceFact(
    Guid FactId,
    long Revision,
    DateTimeOffset Start,
    DateTimeOffset End,
    bool IsFinal,
    string IdentityKey,
    string Title,
    string WorldId,
    string? WorldName,
    string InstanceId);

public sealed class PresenceStateMachine(Func<Guid>? idGenerator = null)
{
    private readonly Func<Guid> _idGenerator = idGenerator ?? Guid.CreateVersion7;
    private VRChatPresenceFact? _current;

    public IReadOnlyList<VRChatPresenceFact> Observe(
        VRChatPresence? presence,
        DateTimeOffset observedAt)
    {
        if (presence is null)
            return FinalizeCurrent(observedAt);
        Validate(presence);

        if (_current is not null &&
            _current.WorldId == presence.WorldId &&
            _current.InstanceId == presence.InstanceId)
        {
            _current = _current with
            {
                Revision = checked(_current.Revision + 1),
                End = observedAt,
                Title = presence.WorldName ?? presence.WorldId,
                WorldName = presence.WorldName
            };
            return [_current];
        }

        var snapshots = new List<VRChatPresenceFact>();
        snapshots.AddRange(FinalizeCurrent(observedAt));
        var factId = _idGenerator();
        if (factId == Guid.Empty || factId.Version != 7)
            throw new InvalidOperationException("VRChat presence Fact ID generator must return a UUIDv7.");
        _current = new VRChatPresenceFact(
            factId,
            1,
            observedAt,
            observedAt,
            false,
            $"{presence.WorldId}|{presence.InstanceId}",
            presence.WorldName ?? presence.WorldId,
            presence.WorldId,
            presence.WorldName,
            presence.InstanceId);
        snapshots.Add(_current);
        return snapshots;
    }

    public IReadOnlyList<VRChatPresenceFact> Stop(DateTimeOffset stoppedAt) =>
        FinalizeCurrent(stoppedAt);

    private IReadOnlyList<VRChatPresenceFact> FinalizeCurrent(DateTimeOffset observedAt)
    {
        if (_current is null)
            return [];
        var final = _current with
        {
            Revision = checked(_current.Revision + 1),
            End = observedAt,
            IsFinal = true
        };
        _current = null;
        return [final];
    }

    private static void Validate(VRChatPresence presence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(presence.WorldId);
        ArgumentException.ThrowIfNullOrWhiteSpace(presence.InstanceId);
    }
}
