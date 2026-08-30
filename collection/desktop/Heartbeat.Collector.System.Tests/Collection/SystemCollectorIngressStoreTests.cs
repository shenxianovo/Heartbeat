using System.Text.Json;
using Heartbeat.Core.DTOs.Input;
using Heartbeat.Collector.System.Collection;
using Heartbeat.Collector.System.Input;

namespace Heartbeat.Collector.System.Tests.Collection;

public sealed class SystemCollectorIngressStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"heartbeat-system-ingress-{Guid.NewGuid():N}");

    [Fact]
    public void AppendBeforeReturnSurvivesRestartAndAcknowledgesOnlyDurablePrefix()
    {
        var path = Path.Combine(_root, "ingress.ndjson");
        var segmentId = Guid.CreateVersion7();
        var eventId = Guid.CreateVersion7();
        var store = SystemCollectorIngressStore.Open(path, 2);
        store.Enqueue(new ForegroundSegmentSnapshot(
            segmentId,
            1,
            "system|foreground",
            "mac:com.example",
            "Example",
            "Document",
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddMinutes(1),
            false));
        Assert.True(store.TryEnqueue(NewInput(eventId)));

        var restarted = SystemCollectorIngressStore.Open(path, 2);

        Assert.True(restarted.PendingFactIds.SetEquals([segmentId, eventId]));
        restarted.AcknowledgeSegmentBatches(restarted.PeekSegmentBatches(10));
        Assert.True(SystemCollectorIngressStore.Open(path, 2).PendingFactIds.SetEquals([eventId]));
    }

    [Fact]
    public void InputCapacityStagesGapWithoutTrimmingRestartablePrefix()
    {
        var path = Path.Combine(_root, "ingress.ndjson");
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        var store = SystemCollectorIngressStore.Open(path, 1);

        Assert.True(store.TryEnqueue(NewInput(first)));
        Assert.False(store.TryEnqueue(NewInput(second)));

        Assert.True(SystemCollectorIngressStore.Open(path, 1).PendingFactIds.SetEquals([first]));
        Assert.Single(SystemCollectorIngressStore.Open(path, 1).PeekInputGaps(10));
    }

    [Fact]
    public void CapacityDecisionAtomicallyStagesEitherInputEventOrStreamGap()
    {
        var path = Path.Combine(_root, "ingress.ndjson");
        var acceptedId = Guid.CreateVersion7();
        var rejectedId = Guid.CreateVersion7();
        var rejectedAt = DateTimeOffset.UnixEpoch.AddSeconds(1);
        var store = SystemCollectorIngressStore.Open(path, 1);

        Assert.Equal(
            SystemInputIngressStageResult.EventStaged,
            store.StageInputEvent(NewInput(acceptedId)));
        Assert.Equal(
            SystemInputIngressStageResult.GapStaged,
            store.StageInputEvent(NewInput(rejectedId, rejectedAt)));

        var restarted = SystemCollectorIngressStore.Open(path, 1);
        Assert.True(restarted.PendingFactIds.SetEquals([acceptedId]));
        var gap = Assert.IsType<SystemInputIngressGap>(Assert.Single(restarted.PeekInputGaps(10)).Gap);
        Assert.Equal(rejectedAt, gap.Start);
        Assert.Equal(rejectedAt.AddTicks(1), gap.End);
        Assert.Equal(1, gap.EstimatedFactsLost);
        Assert.Equal(7, gap.GapId.Version);
    }

    [Fact]
    public void DurableJournalRotatesIntoBoundedSegmentsInsteadOfRewritingItsFullHistory()
    {
        var path = Path.Combine(_root, "ingress.ndjson");
        var store = SystemCollectorIngressStore.Open(path, 1_000);

        for (var index = 0; index < 400; index++)
        {
            Assert.Equal(
                SystemInputIngressStageResult.EventStaged,
                store.StageInputEvent(NewInput(
                    Guid.CreateVersion7(),
                    DateTimeOffset.UnixEpoch.AddTicks(index))));
        }

        var segments = Directory.GetFiles(_root, "ingress.ndjson*")
            .Where(candidate => !candidate.EndsWith(".tmp", StringComparison.Ordinal))
            .ToArray();
        Assert.True(segments.Length > 1);
        Assert.All(segments, segment => Assert.InRange(new FileInfo(segment).Length, 1, 36 * 1024));
        var restarted = SystemCollectorIngressStore.Open(path, 1_000);
        Assert.Equal(400, restarted.PeekInputEvents(1_000).Count);
        restarted.AcknowledgeInputDeliveries(restarted.PeekInputDeliveries(1_000));

        Assert.Single(Directory.GetFiles(_root, "ingress.ndjson*"));
        Assert.Empty(SystemCollectorIngressStore.Open(path, 1_000).PeekInputDeliveries(1_000));
    }

    [Fact]
    public void InputBatchPersistsCapacityDecisionsAsOneRestartableMutation()
    {
        var path = Path.Combine(_root, "ingress.ndjson");
        var store = SystemCollectorIngressStore.Open(path, 2);
        var inputs = Enumerable.Range(0, 4)
            .Select(index => NewInput(
                Guid.CreateVersion7(),
                DateTimeOffset.UnixEpoch.AddTicks(index)))
            .ToArray();

        Assert.Equal(2, store.StageInputEvents(inputs));

        var restarted = SystemCollectorIngressStore.Open(path, 2);
        Assert.Equal(2, restarted.PeekInputEvents(10).Count);
        Assert.Equal(2, restarted.PeekInputGaps(10).Count);
        Assert.Single(File.ReadLines(path));
    }

    [Fact]
    public void RotationCheckpointSurvivesFactAckAndRestartAsFinalPointPlusGap()
    {
        var path = Path.Combine(_root, "ingress.ndjson");
        var boundary = new DateTimeOffset(2026, 8, 30, 10, 0, 0, TimeSpan.Zero);
        var recoveredAt = boundary.AddMinutes(2);
        var finalized = NewSegment(
            Guid.CreateVersion7(),
            DateTimeOffset.UnixEpoch,
            boundary,
            revision: 1,
            isFinal: true);
        var continuation = NewSegment(
            Guid.CreateVersion7(),
            boundary,
            boundary,
            revision: 1,
            isFinal: false);
        var store = SystemCollectorIngressStore.Open(path, 2);

        store.StageSegmentBatch([finalized, continuation]);
        var staged = Assert.Single(store.PeekSegmentBatches(10));
        store.AcknowledgeSegmentBatches([staged]);

        var restarted = SystemCollectorIngressStore.Open(path, 2);
        Assert.Empty(restarted.PendingFactIds);
        Assert.Equal(continuation, restarted.ActiveSegmentCheckpoint);
        restarted.RecoverInterruptedSegment(recoveredAt);

        var recovered = SystemCollectorIngressStore.Open(path, 2);
        var replay = Assert.Single(recovered.PeekSegmentBatches(10)).Snapshots;
        var recoveredFinal = Assert.Single(replay);
        Assert.Equal(continuation.FactId, recoveredFinal.FactId);
        Assert.Equal(2, recoveredFinal.Revision);
        Assert.Equal(boundary, recoveredFinal.Start);
        Assert.Equal(boundary, recoveredFinal.End);
        Assert.True(recoveredFinal.IsFinal);
        var gap = Assert.Single(recovered.PeekSegmentGaps(10)).Gap;
        Assert.Equal(boundary, gap.Start);
        Assert.Equal(recoveredAt, gap.End);
        Assert.Equal("process_restart", gap.Reason);
        Assert.Null(recovered.ActiveSegmentCheckpoint);
    }

    [Fact]
    public void OpenRepairsMalformedTailBeforeAppendAndSecondRestart()
    {
        var path = Path.Combine(_root, "ingress.ndjson");
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        var store = SystemCollectorIngressStore.Open(path, 2);
        Assert.Equal(
            SystemInputIngressStageResult.EventStaged,
            store.StageInputEvent(NewInput(first)));
        File.AppendAllText(path, "{\"entryId\":");

        var repaired = SystemCollectorIngressStore.Open(path, 2);
        Assert.Equal(
            SystemInputIngressStageResult.EventStaged,
            repaired.StageInputEvent(NewInput(second)));

        var restarted = SystemCollectorIngressStore.Open(path, 2);
        Assert.True(restarted.PendingFactIds.SetEquals([first, second]));
    }

    [Fact]
    public void OpenRepairsValidJsonTailWithoutNewlineBeforeAppendAndSecondRestart()
    {
        var path = Path.Combine(_root, "ingress.ndjson");
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        var store = SystemCollectorIngressStore.Open(path, 2);
        Assert.Equal(
            SystemInputIngressStageResult.EventStaged,
            store.StageInputEvent(NewInput(first)));
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read))
        {
            Assert.Equal((byte)'\n', ReadLastByte(path));
            stream.SetLength(stream.Length - 1);
        }

        var repaired = SystemCollectorIngressStore.Open(path, 2);
        Assert.Equal(
            SystemInputIngressStageResult.EventStaged,
            repaired.StageInputEvent(NewInput(second)));

        var restarted = SystemCollectorIngressStore.Open(path, 2);
        Assert.True(restarted.PendingFactIds.SetEquals([first, second]));
    }

    [Fact]
    public void OpenStillRejectsMalformedMiddleLine()
    {
        var path = Path.Combine(_root, "ingress.ndjson");
        var store = SystemCollectorIngressStore.Open(path, 2);
        store.StageInputEvent(NewInput(Guid.CreateVersion7()));
        var valid = File.ReadAllText(path);
        File.WriteAllText(path, valid + "{\"entryId\":\n" + valid);

        Assert.Throws<JsonException>(() => SystemCollectorIngressStore.Open(path, 2));
    }

    [Fact]
    public async Task DeadlineFenceRejectsAPreparedJournalMutationThatReturnsLate()
    {
        var path = Path.Combine(_root, "ingress.ndjson");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var fence = new SystemCollectorIngressCommitFence();
        void BeforeCommit()
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(5));
        }
        var store = SystemCollectorIngressStore.Open(path, 2, fence, BeforeCommit);
        var staging = Task.Run(() => store.StageInputEvent(NewInput(Guid.CreateVersion7())));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));

        fence.Fence();
        release.Set();

        await Assert.ThrowsAsync<OperationCanceledException>(() => staging);
        Assert.False(File.Exists(path));
        Assert.Empty(SystemCollectorIngressStore.Open(path, 2).PeekInputDeliveries(10));
    }

    private static InputEventItem NewInput(Guid id, DateTimeOffset? timestamp = null) => new()
    {
        Id = id,
        EventType = InputEventType.MouseButton,
        CodeSet = InputCodeSets.HeartbeatKeyPositionV1,
        Code = 1,
        Timestamp = timestamp ?? DateTimeOffset.UnixEpoch
    };

    private static byte ReadLastByte(string path)
    {
        using var stream = File.OpenRead(path);
        stream.Position = stream.Length - 1;
        return checked((byte)stream.ReadByte());
    }

    private static ForegroundSegmentSnapshot NewSegment(
        Guid id,
        DateTimeOffset start,
        DateTimeOffset end,
        long revision,
        bool isFinal) => new(
            id,
            revision,
            "system|win:code|main.cs",
            "win:code",
            "Code",
            "main.cs",
            start,
            end,
            isFinal);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
