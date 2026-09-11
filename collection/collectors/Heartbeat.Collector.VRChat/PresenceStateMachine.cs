using Heartbeat.Core;

namespace Heartbeat.Collector.VRChat;

public sealed record VRChatPresence(
    string WorldId,
    string? WorldName,
    string InstanceId,
    string? ObservedAccountId = null);

public sealed record VRChatPresenceFact(
    Guid FactId,
    long Revision,
    DateTimeOffset Start,
    DateTimeOffset End,
    bool IsFinal,
    string ActivityKey,
    string Title,
    string WorldId,
    string? WorldName,
    string InstanceId,
    string? ObservedAccountId = null,
    bool IsNativeObservation = false,
    Guid? CollectorId = null);

public sealed class PresenceStateMachine(Func<Guid>? idGenerator = null, Guid? collectorId = null)
{
    private readonly PresenceFactPublisher _publisher = new(idGenerator, collectorId ?? Guid.CreateVersion7());

    public IReadOnlyList<VRChatPresenceFact> Observe(VRChatPresence? presence, DateTimeOffset observedAt)
    {
        if (presence is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(presence.WorldId);
            ArgumentException.ThrowIfNullOrWhiteSpace(presence.InstanceId);
            if (!Heartbeat.Core.DTOs.Facts.ServiceAccountReference.IsVRChatAccountId(presence.ObservedAccountId))
                throw new ArgumentException("Presence requires the observed service account ID.");
        }
        var current = _publisher.Current;
        var continues = current is not null && presence is not null &&
            current.ObservedAccountId == presence.ObservedAccountId &&
            current.WorldId == presence.WorldId && current.InstanceId == presence.InstanceId;
        return _publisher.Publish(presence, continues, observedAt);
    }

    public IReadOnlyList<VRChatPresenceFact> Stop(DateTimeOffset stoppedAt) => _publisher.Stop(stoppedAt);
    public void Restore(VRChatPresenceFact active) => _publisher.Restore(active);
    public VRChatPresenceFact FinalizeRestored() => _publisher.FinalizeRestored();
}
