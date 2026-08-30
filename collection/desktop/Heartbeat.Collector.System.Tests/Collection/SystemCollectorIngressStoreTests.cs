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
        restarted.AcknowledgeSegments(restarted.PeekSegments(10));
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
        var gap = Assert.Single(restarted.PeekInputGaps(10)).Gap;
        Assert.Equal(rejectedAt, gap.Start);
        Assert.Equal(rejectedAt.AddTicks(1), gap.End);
        Assert.Equal(1, gap.EstimatedFactsLost);
        Assert.Equal(7, gap.GapId.Version);
    }

    private static InputEventItem NewInput(Guid id, DateTimeOffset? timestamp = null) => new()
    {
        Id = id,
        EventType = InputEventType.MouseButton,
        CodeSet = InputCodeSets.HeartbeatKeyPositionV1,
        Code = 1,
        Timestamp = timestamp ?? DateTimeOffset.UnixEpoch
    };

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
