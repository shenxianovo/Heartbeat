using Heartbeat.Collector.VRChat;

namespace Heartbeat.Collector.VRChat.Tests;

public sealed class PresenceStateMachineTests
{
    [Fact]
    public void SameInstanceExtendsStableFactAndSwitchFinalizesBeforeOpeningTheNext()
    {
        var ids = new Queue<Guid>(
        [
            Guid.Parse("0198d5eb-fc31-7d7b-8bf0-c2d009ec8999"),
            Guid.Parse("0198d5ec-0ba4-78ea-862f-ea1f16ec2e93")
        ]);
        var machine = new PresenceStateMachine(ids.Dequeue);
        var start = new DateTimeOffset(2026, 8, 27, 10, 0, 0, TimeSpan.Zero);

        var opened = Assert.Single(machine.Observe(
            new VRChatPresence("wrld_alpha", "Alpha", "instance:one"),
            start));
        var extended = Assert.Single(machine.Observe(
            new VRChatPresence("wrld_alpha", "Alpha", "instance:one"),
            start.AddMinutes(1)));
        var switched = machine.Observe(
            new VRChatPresence("wrld_alpha", "Alpha", "instance:two"),
            start.AddMinutes(2));

        Assert.Equal(opened.FactId, extended.FactId);
        Assert.Equal(1, opened.Revision);
        Assert.Equal(2, extended.Revision);
        Assert.False(opened.IsFinal);
        Assert.False(extended.IsFinal);
        Assert.Equal(2, switched.Count);
        Assert.Equal(opened.FactId, switched[0].FactId);
        Assert.Equal(3, switched[0].Revision);
        Assert.True(switched[0].IsFinal);
        Assert.Equal("instance:one", switched[0].InstanceId);
        Assert.NotEqual(opened.FactId, switched[1].FactId);
        Assert.Equal(1, switched[1].Revision);
        Assert.False(switched[1].IsFinal);
        Assert.Equal("instance:two", switched[1].InstanceId);
    }

    [Fact]
    public void GoingOfflineAndStoppingEmitOneFinalSnapshot()
    {
        var machine = new PresenceStateMachine(() => Guid.CreateVersion7());
        var start = new DateTimeOffset(2026, 8, 27, 10, 0, 0, TimeSpan.Zero);
        machine.Observe(new VRChatPresence("wrld_alpha", "Alpha", "instance:one"), start);

        var offline = Assert.Single(machine.Observe(null, start.AddMinutes(1)));

        Assert.True(offline.IsFinal);
        Assert.Empty(machine.Stop(start.AddMinutes(2)));
    }
}
