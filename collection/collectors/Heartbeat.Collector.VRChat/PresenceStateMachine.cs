using Heartbeat.Core;

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
    private bool _isRestored;

    public IReadOnlyList<VRChatPresenceFact> Observe(
        VRChatPresence? presence,
        DateTimeOffset observedAt)
    {
        if (_isRestored)
            throw new InvalidOperationException("A restored VRChat presence must be finalized before observing again.");
        if (presence is null)
            return FinalizeCurrent(observedAt);
        Validate(presence);

        if (_current is not null &&
            _current.WorldId == presence.WorldId &&
            _current.InstanceId == presence.InstanceId)
        {
            if (observedAt >= _current.Start + SegmentRotationPolicy.RotateAfter)
                return RotateCurrent(presence, observedAt);

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
        _current = NewFact(presence, observedAt, observedAt, isFinal: false);
        snapshots.Add(_current);
        return snapshots;
    }

    public IReadOnlyList<VRChatPresenceFact> Stop(DateTimeOffset stoppedAt) =>
        _isRestored ? [FinalizeRestored()] : FinalizeCurrent(stoppedAt);

    public void Restore(VRChatPresenceFact active)
    {
        ArgumentNullException.ThrowIfNull(active);
        if (_current is not null)
            throw new InvalidOperationException("VRChat presence is already active.");
        if (active.FactId == Guid.Empty || active.Revision <= 0 || active.IsFinal || active.End < active.Start)
            throw new ArgumentException("Restored VRChat presence must be an active valid snapshot.", nameof(active));
        _current = active;
        _isRestored = true;
    }

    public VRChatPresenceFact FinalizeRestored()
    {
        if (_current is null || !_isRestored)
            throw new InvalidOperationException("No restored VRChat presence is active.");
        var finalized = _current with
        {
            Revision = checked(_current.Revision + 1),
            IsFinal = true
        };
        _current = null;
        _isRestored = false;
        return finalized;
    }

    private IReadOnlyList<VRChatPresenceFact> FinalizeCurrent(DateTimeOffset observedAt)
    {
        if (_current is null)
            return [];

        var snapshots = new List<VRChatPresenceFact>();
        var boundary = _current.Start + SegmentRotationPolicy.RotateAfter;
        if (observedAt < boundary)
        {
            snapshots.Add(_current with
            {
                Revision = checked(_current.Revision + 1),
                End = observedAt,
                IsFinal = true
            });
            _current = null;
            return snapshots;
        }

        var presence = ToPresence(_current);
        snapshots.Add(_current with
        {
            Revision = checked(_current.Revision + 1),
            End = boundary,
            IsFinal = true
        });
        while (observedAt > boundary)
        {
            var nextBoundary = boundary + SegmentRotationPolicy.RotateAfter;
            var end = observedAt < nextBoundary ? observedAt : nextBoundary;
            snapshots.Add(NewFact(presence, boundary, end, isFinal: true));
            boundary = end;
        }
        _current = null;
        return snapshots;
    }

    private IReadOnlyList<VRChatPresenceFact> RotateCurrent(
        VRChatPresence presence,
        DateTimeOffset observedAt)
    {
        var snapshots = new List<VRChatPresenceFact>();
        var boundary = _current!.Start + SegmentRotationPolicy.RotateAfter;
        snapshots.Add(_current with
        {
            Revision = checked(_current.Revision + 1),
            End = boundary,
            IsFinal = true,
            Title = presence.WorldName ?? presence.WorldId,
            WorldName = presence.WorldName
        });

        while (observedAt - boundary >= SegmentRotationPolicy.RotateAfter)
        {
            var nextBoundary = boundary + SegmentRotationPolicy.RotateAfter;
            snapshots.Add(NewFact(presence, boundary, nextBoundary, isFinal: true));
            boundary = nextBoundary;
        }

        _current = NewFact(presence, boundary, observedAt, isFinal: false);
        snapshots.Add(_current);
        return snapshots;
    }

    private VRChatPresenceFact NewFact(
        VRChatPresence presence,
        DateTimeOffset start,
        DateTimeOffset end,
        bool isFinal)
    {
        var factId = _idGenerator();
        if (factId == Guid.Empty || factId.Version != 7)
            throw new InvalidOperationException("VRChat presence Fact ID generator must return a UUIDv7.");
        return new VRChatPresenceFact(
            factId,
            1,
            start,
            end,
            isFinal,
            $"{presence.WorldId}|{presence.InstanceId}",
            presence.WorldName ?? presence.WorldId,
            presence.WorldId,
            presence.WorldName,
            presence.InstanceId);
    }

    private static VRChatPresence ToPresence(VRChatPresenceFact fact) =>
        new(fact.WorldId, fact.WorldName, fact.InstanceId);

    private static void Validate(VRChatPresence presence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(presence.WorldId);
        ArgumentException.ThrowIfNullOrWhiteSpace(presence.InstanceId);
    }
}
