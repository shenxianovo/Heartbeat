using Heartbeat.Collection.CollectorProtocol;
using Heartbeat.Core.DTOs.Facts;

namespace Heartbeat.Collector.VRChat.Tests;

public sealed class VRChatObservationProducerTests
{
    [Fact]
    public void PresenceProducesIndependentFactAndSameEndFinalAdvancesRevision()
    {
        var observer = Guid.NewGuid();
        var machine = new PresenceStateMachine(collectorId: observer);
        var now = DateTimeOffset.UtcNow;
        var presence = new VRChatPresence("world", "World", "instance", "usr_11111111-1111-4111-8111-111111111111");
        var opened = Assert.Single(machine.Observe(presence, now));
        var fact = VRChatManagedCollector.ToFact(opened, observer);
        Assert.Equal("segment", fact.Kind);
        Assert.Equal("vrchat.account", fact.Source);
        Assert.Equal(observer, fact.CollectorId);
        Assert.Equal(new ObservationObjectReference("account", "vrchat", presence.ObservedAccountId!), fact.Foi);
        Assert.Equal("account-location", fact.Aspect);
        Assert.Empty(fact.Relations!);
        var finalized = VRChatManagedCollector.ToFact(Assert.Single(machine.Stop(now)), observer);
        Assert.Equal(fact.FactId, finalized.FactId);
        Assert.Equal(2, finalized.Revision);
        Assert.Equal(now, Assert.IsType<CollectorSegmentFactTime>(finalized.Time).End);
        Assert.True(Assert.IsType<CollectorSegmentFactTime>(finalized.Time).IsFinal);
    }

    [Fact]
    public void RestoredNativePresenceRetainsItsObserverBeforeNewAccountStarts()
    {
        var originalObserver = Guid.NewGuid();
        var currentObserver = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var original = new PresenceStateMachine(collectorId: originalObserver);
        var active = Assert.Single(original.Observe(new VRChatPresence("world", null, "instance",
            "usr_11111111-1111-4111-8111-111111111111"), now));
        var restarted = new PresenceStateMachine(collectorId: currentObserver);
        restarted.Restore(active);
        var final = VRChatManagedCollector.ToFact(restarted.FinalizeRestored(), currentObserver);
        Assert.Equal(originalObserver, final.CollectorId);
        Assert.Equal(active.FactId, final.FactId);
        Assert.Equal(active.ObservedAccountId, final.Foi!.Key);
        Assert.Equal(now, Assert.IsType<CollectorSegmentFactTime>(final.Time).End);
        Assert.False(final.Payload.TryGetProperty("worldName", out _));
        var next = VRChatManagedCollector.ToFact(Assert.Single(restarted.Observe(
            new VRChatPresence("world", "Known name", "instance", "usr_22222222-2222-4222-8222-222222222222"),
            now.AddHours(1))), currentObserver);
        Assert.NotEqual(final.FactId, next.FactId);
        Assert.Equal(currentObserver, next.CollectorId);
        Assert.NotEqual(final.Foi, next.Foi);
    }
}
