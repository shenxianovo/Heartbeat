using Heartbeat.Core;
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

    [Fact]
    public void SamePresenceAtRotationBoundary_FinalizesAndOpensDurableContinuation()
    {
        var ids = new Queue<Guid>(
        [
            Guid.Parse("0198d5eb-fc31-7d7b-8bf0-c2d009ec8999"),
            Guid.Parse("0198d5ec-0ba4-78ea-862f-ea1f16ec2e93")
        ]);
        var machine = new PresenceStateMachine(ids.Dequeue);
        var start = new DateTimeOffset(2026, 8, 27, 10, 0, 0, TimeSpan.Zero);
        var presence = new VRChatPresence("wrld_alpha", "Alpha", "instance:one");
        var opened = Assert.Single(machine.Observe(presence, start));

        var rotated = machine.Observe(presence, start + SegmentRotationPolicy.RotateAfter);

        Assert.Equal(2, rotated.Count);
        Assert.Equal(opened.FactId, rotated[0].FactId);
        Assert.True(rotated[0].IsFinal);
        Assert.Equal(start + SegmentRotationPolicy.RotateAfter, rotated[0].End);
        Assert.NotEqual(opened.FactId, rotated[1].FactId);
        Assert.Equal(7, rotated[1].FactId.Version);
        Assert.Equal(1, rotated[1].Revision);
        Assert.False(rotated[1].IsFinal);
        Assert.Equal(rotated[0].End, rotated[1].Start);
        Assert.Equal(rotated[1].Start, rotated[1].End);
        Assert.Equal(opened.IdentityKey, rotated[1].IdentityKey);
    }

    [Fact]
    public void SamePresenceAfterMultipleBoundaries_EmitsContinuousBoundedChunks()
    {
        var ids = new Queue<Guid>(
        [
            Guid.Parse("0198d5eb-fc31-7d7b-8bf0-c2d009ec8999"),
            Guid.Parse("0198d5ec-0ba4-78ea-862f-ea1f16ec2e93"),
            Guid.Parse("0198d5ec-1aa4-7bcb-8123-3292df398ac8")
        ]);
        var machine = new PresenceStateMachine(ids.Dequeue);
        var start = new DateTimeOffset(2026, 8, 27, 10, 0, 0, TimeSpan.Zero);
        var presence = new VRChatPresence("wrld_alpha", "Alpha", "instance:one");
        machine.Observe(presence, start);
        var elapsed = SegmentRotationPolicy.RotateAfter * 2 + TimeSpan.FromMinutes(5);

        var snapshots = machine.Observe(presence, start + elapsed);

        Assert.Equal(3, snapshots.Count);
        Assert.True(snapshots[0].IsFinal);
        Assert.True(snapshots[1].IsFinal);
        Assert.False(snapshots[2].IsFinal);
        Assert.Equal(snapshots[0].End, snapshots[1].Start);
        Assert.Equal(snapshots[1].End, snapshots[2].Start);
        Assert.All(snapshots, snapshot =>
            Assert.True(snapshot.End - snapshot.Start <= SegmentRotationPolicy.RotateAfter));
        Assert.Equal(elapsed, snapshots.Aggregate(
            TimeSpan.Zero, (total, snapshot) => total + (snapshot.End - snapshot.Start)));
        Assert.Equal([2L, 1L, 1L], snapshots.Select(snapshot => snapshot.Revision));
    }

    [Fact]
    public void PresenceChangeAtRotationBoundary_FinalizesOldPresenceOnlyOnce()
    {
        var ids = new Queue<Guid>(
        [
            Guid.Parse("0198d5eb-fc31-7d7b-8bf0-c2d009ec8999"),
            Guid.Parse("0198d5ec-0ba4-78ea-862f-ea1f16ec2e93")
        ]);
        var machine = new PresenceStateMachine(ids.Dequeue);
        var start = new DateTimeOffset(2026, 8, 27, 10, 0, 0, TimeSpan.Zero);
        machine.Observe(new VRChatPresence("wrld_alpha", "Alpha", "instance:one"), start);

        var switched = machine.Observe(
            new VRChatPresence("wrld_beta", "Beta", "instance:two"),
            start + SegmentRotationPolicy.RotateAfter);

        Assert.Equal(2, switched.Count);
        Assert.True(switched[0].IsFinal);
        Assert.Equal("wrld_alpha", switched[0].WorldId);
        Assert.False(switched[1].IsFinal);
        Assert.Equal("wrld_beta", switched[1].WorldId);
        Assert.Equal(switched[0].End, switched[1].Start);
        var stopped = Assert.Single(machine.Stop(switched[1].Start.AddMinutes(1)));
        Assert.Equal(switched[1].FactId, stopped.FactId);
        Assert.Equal("wrld_beta", stopped.WorldId);
    }

    [Fact]
    public void StopAfterRotationBoundary_FinalizesAllChunksWithoutOpeningAnother()
    {
        var ids = new Queue<Guid>(
        [
            Guid.Parse("0198d5eb-fc31-7d7b-8bf0-c2d009ec8999"),
            Guid.Parse("0198d5ec-0ba4-78ea-862f-ea1f16ec2e93")
        ]);
        var machine = new PresenceStateMachine(ids.Dequeue);
        var start = new DateTimeOffset(2026, 8, 27, 10, 0, 0, TimeSpan.Zero);
        machine.Observe(new VRChatPresence("wrld_alpha", "Alpha", "instance:one"), start);
        var elapsed = SegmentRotationPolicy.RotateAfter + TimeSpan.FromMinutes(5);

        var stopped = machine.Stop(start + elapsed);

        Assert.Equal(2, stopped.Count);
        Assert.All(stopped, snapshot => Assert.True(snapshot.IsFinal));
        Assert.Equal(stopped[0].End, stopped[1].Start);
        Assert.Equal(elapsed, stopped.Aggregate(
            TimeSpan.Zero, (total, snapshot) => total + (snapshot.End - snapshot.Start)));
        Assert.Empty(machine.Stop(start + elapsed + TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void RestoredActiveSnapshot_FinalizesAtPersistedEndWithoutBackwardRotation()
    {
        var machine = new PresenceStateMachine(() => Guid.CreateVersion7());
        var start = new DateTimeOffset(2026, 8, 27, 10, 0, 0, TimeSpan.Zero);
        var restored = new VRChatPresenceFact(
            Guid.Parse("0198d5eb-fc31-7d7b-8bf0-c2d009ec8999"),
            4,
            start,
            start + SegmentRotationPolicy.RotateAfter + TimeSpan.FromMinutes(30),
            false,
            "wrld_alpha|instance:one",
            "Alpha",
            "wrld_alpha",
            "Alpha",
            "instance:one");
        machine.Restore(restored);

        var finalized = machine.FinalizeRestored();

        Assert.Equal(restored.FactId, finalized.FactId);
        Assert.Equal(5, finalized.Revision);
        Assert.Equal(restored.Start, finalized.Start);
        Assert.Equal(restored.End, finalized.End);
        Assert.True(finalized.IsFinal);
        Assert.Empty(machine.Stop(restored.End + TimeSpan.FromHours(2)));
    }
}
