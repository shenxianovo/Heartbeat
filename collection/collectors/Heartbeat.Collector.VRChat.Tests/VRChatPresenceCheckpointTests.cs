using Heartbeat.Core;
using Heartbeat.Collector.VRChat;
using System.Text.Json;

namespace Heartbeat.Collector.VRChat.Tests;

public sealed class VRChatPresenceCheckpointTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"heartbeat-vrchat-presence-{Guid.NewGuid():N}");

    [Fact]
    public void RestartRestoresTheActiveDomainSnapshot()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "presence.json");
        var start = new DateTimeOffset(2026, 8, 27, 10, 0, 0, TimeSpan.Zero);
        var active = Fact(start);
        var checkpoint = VRChatPresenceCheckpoint.Open(path, start);

        checkpoint.Stage([active]);
        checkpoint.Acknowledge(active);
        var reopened = VRChatPresenceCheckpoint.Open(path, start.AddMinutes(3));

        Assert.Equal(active, reopened.Active);
        Assert.Empty(reopened.PendingFacts);
        Assert.Null(reopened.RecoveryGap);
    }

    [Fact]
    public void CorruptCheckpointIsQuarantinedAndReturnsARecoveryGap()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "presence.json");
        var lastGoodBoundary = new DateTimeOffset(2026, 8, 27, 10, 0, 0, TimeSpan.Zero);
        var recoveredAt = lastGoodBoundary.AddMinutes(4);
        File.WriteAllText(path, "{truncated");
        File.SetLastWriteTimeUtc(path, lastGoodBoundary.UtcDateTime);

        var recovered = VRChatPresenceCheckpoint.Open(path, recoveredAt);

        Assert.Null(recovered.Active);
        var recoveryGap = Assert.IsType<VRChatPresenceRecoveryGap>(recovered.RecoveryGap);
        Assert.Equal("presence_checkpoint_corrupted", recoveryGap.Reason);
        Assert.Equal(lastGoodBoundary, recoveryGap.Start);
        Assert.Equal(recoveredAt, recoveryGap.End);
        Assert.Equal(7, recoveryGap.GapId.Version);
        Assert.Equal([recoveryGap], recovered.PendingGaps);
        Assert.Single(Directory.EnumerateFiles(_directory, "presence.json.corrupt-*"));

        recovered.Acknowledge(recoveryGap);
        Assert.Empty(VRChatPresenceCheckpoint.Open(path, recoveredAt).PendingGaps);
    }

    [Fact]
    public void RotationTransitionRemainsReplayableAcrossEveryCheckpointCrashPoint()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "presence.json");
        var start = new DateTimeOffset(2026, 8, 27, 10, 0, 0, TimeSpan.Zero);
        var ids = new Queue<Guid>(
        [
            Guid.Parse("0198d5eb-fc31-7d7b-8bf0-c2d009ec8999"),
            Guid.Parse("0198d5ec-0ba4-78ea-862f-ea1f16ec2e93")
        ]);
        var machine = new PresenceStateMachine(ids.Dequeue);
        var checkpoint = VRChatPresenceCheckpoint.Open(path, start);
        var presence = new VRChatPresence("wrld_alpha", "Alpha", "instance:one");
        var opened = Assert.Single(machine.Observe(presence, start));
        checkpoint.Stage([opened]);
        checkpoint.Acknowledge(opened);

        var rotated = machine.Observe(presence, start + SegmentRotationPolicy.RotateAfter);
        checkpoint.Stage(rotated);

        var beforePublish = VRChatPresenceCheckpoint.Open(
            path, start + SegmentRotationPolicy.RotateAfter);
        Assert.Equal(rotated, beforePublish.PendingFacts);
        Assert.Equal(rotated[1], beforePublish.Active);

        beforePublish.Acknowledge(rotated[0]);
        var afterFinalAcknowledgement = VRChatPresenceCheckpoint.Open(
            path, start + SegmentRotationPolicy.RotateAfter);
        Assert.Equal([rotated[1]], afterFinalAcknowledgement.PendingFacts);
        Assert.Equal(rotated[1], afterFinalAcknowledgement.Active);

        afterFinalAcknowledgement.Acknowledge(rotated[1]);
        var afterAllAcknowledgements = VRChatPresenceCheckpoint.Open(
            path, start + SegmentRotationPolicy.RotateAfter);

        Assert.True(rotated[0].IsFinal);
        Assert.Empty(afterAllAcknowledgements.PendingFacts);
        Assert.Equal(rotated[1], afterAllAcknowledgements.Active);
        Assert.NotEqual(rotated[0].FactId, afterAllAcknowledgements.Active!.FactId);
        Assert.Equal(1, afterAllAcknowledgements.Active.Revision);
    }

    [Fact]
    public void CurrentV1CheckpointLoadsWithoutShrinkingAndRewritesAtomicallyAsV2()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "presence.json");
        var start = new DateTimeOffset(2026, 8, 27, 10, 0, 0, TimeSpan.Zero);
        var active = Fact(start) with
        {
            End = start + SegmentRotationPolicy.RotateAfter + TimeSpan.FromMinutes(30)
        };
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            SchemaVersion = 1,
            Active = active
        }));

        var checkpoint = VRChatPresenceCheckpoint.Open(path, active.End + TimeSpan.FromMinutes(2));
        Assert.Equal(active, checkpoint.Active);
        Assert.Empty(checkpoint.PendingFacts);

        var finalized = active with { Revision = active.Revision + 1, IsFinal = true };
        checkpoint.Stage([finalized]);
        using var rewritten = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(2, rewritten.RootElement.GetProperty("SchemaVersion").GetInt32());
        Assert.Equal(active.End, checkpoint.PendingFacts[0].End);
    }

    [Fact]
    public void RestartFinalAndDowntimeGapRemainDurableAcrossAcknowledgementCrashPoints()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "presence.json");
        var start = new DateTimeOffset(2026, 8, 27, 10, 0, 0, TimeSpan.Zero);
        var active = Fact(start);
        var checkpoint = VRChatPresenceCheckpoint.Open(path, active.End);
        checkpoint.Stage([active]);
        checkpoint.Acknowledge(active);
        var finalized = active with { Revision = active.Revision + 1, IsFinal = true };
        var recoveredAt = active.End + TimeSpan.FromHours(2);
        var gap = new VRChatPresenceRecoveryGap(
            Guid.Parse("0198d5ec-1aa4-7bcb-8123-3292df398ac8"),
            active.End,
            recoveredAt,
            "process_restart");

        checkpoint.Stage([finalized], [gap]);
        var beforePublish = VRChatPresenceCheckpoint.Open(path, recoveredAt);
        Assert.Null(beforePublish.Active);
        Assert.Equal([finalized], beforePublish.PendingFacts);
        Assert.Equal([gap], beforePublish.PendingGaps);

        beforePublish.Acknowledge(finalized);
        var afterFinalAcknowledgement = VRChatPresenceCheckpoint.Open(path, recoveredAt);
        Assert.Empty(afterFinalAcknowledgement.PendingFacts);
        Assert.Equal([gap], afterFinalAcknowledgement.PendingGaps);

        afterFinalAcknowledgement.Acknowledge(gap);
        var complete = VRChatPresenceCheckpoint.Open(path, recoveredAt);
        Assert.Empty(complete.PendingFacts);
        Assert.Empty(complete.PendingGaps);
    }

    private static VRChatPresenceFact Fact(DateTimeOffset start) => new(
        Guid.Parse("0198d5eb-fc31-7d7b-8bf0-c2d009ec8999"),
        2,
        start,
        start.AddMinutes(1),
        false,
        "wrld_alpha|instance:one",
        "Alpha",
        "wrld_alpha",
        "Alpha",
        "instance:one");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
