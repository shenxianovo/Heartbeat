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
    public void InputCapacityRejectsWithoutTrimmingRestartablePrefix()
    {
        var path = Path.Combine(_root, "ingress.ndjson");
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        var store = SystemCollectorIngressStore.Open(path, 1);

        Assert.True(store.TryEnqueue(NewInput(first)));
        Assert.False(store.TryEnqueue(NewInput(second)));

        Assert.True(SystemCollectorIngressStore.Open(path, 1).PendingFactIds.SetEquals([first]));
    }

    private static InputEventItem NewInput(Guid id) => new()
    {
        Id = id,
        EventType = InputEventType.MouseButton,
        CodeSet = InputCodeSets.HeartbeatKeyPositionV1,
        Code = 1,
        Timestamp = DateTimeOffset.UnixEpoch
    };

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
