using System.Text.Json;
using Heartbeat.Collector.System.Input;
using Heartbeat.Collector.System.Collection;
using Heartbeat.Collection.Hub.Time;
using Heartbeat.Collection.Hub.Upload;
using Heartbeat.Collection.Hub.Storage;
using Heartbeat.Core.DTOs.Input;

namespace Heartbeat.Collector.System.Tests.Input;

public class InputEventBufferTests
{
    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UnixEpoch;
    }

    private sealed class RecordingPublisher : ISystemInputEventPublisher
    {
        public List<InputEventItem> Items { get; } = [];
        public void Publish(InputEventItem item) => Items.Add(item);
    }

    private static InputEventBuffer NewBuffer(out RecordingPublisher publisher)
    {
        publisher = new RecordingPublisher();
        return new InputEventBuffer(new FakeClock(), publisher: publisher);
    }

    [Fact]
    public void NewInputWithoutPublisherCannotEnterLegacyUploadBuffer()
    {
        var buffer = new InputEventBuffer(new FakeClock());

        Assert.Throws<InvalidOperationException>(() => buffer.OnMouseButton(1));
        Assert.Empty(buffer.ReadAll());
        Assert.Equal(0, buffer.Count);
    }

    [Fact]
    public void OnKeyDown_RecordsEvent()
    {
        var buf = NewBuffer(out var published);

        Assert.True(buf.OnKeyDown(InputKeyPosition.KeyA));

        var items = published.Items;
        Assert.Single(items);
        Assert.Equal(InputEventType.KeyDown, items[0].EventType);
        Assert.Equal((short)InputKeyPosition.KeyA, items[0].Code);
        Assert.Equal(InputCodeSets.HeartbeatKeyPositionV1, items[0].CodeSet);
    }

    [Fact]
    public void OnKeyDown_FiltersAutoRepeat_UntilKeyUp()
    {
        var buf = NewBuffer(out var published);

        Assert.True(buf.OnKeyDown(InputKeyPosition.KeyA));   // 首次记录
        Assert.False(buf.OnKeyDown(InputKeyPosition.KeyA));  // 自动重复，丢弃
        Assert.False(buf.OnKeyDown(InputKeyPosition.KeyA));  // 仍丢弃

        buf.OnKeyUp(InputKeyPosition.KeyA);
        Assert.True(buf.OnKeyDown(InputKeyPosition.KeyA));   // 抬起后再按，重新记录

        Assert.Equal(2, published.Items.Count);
    }

    [Fact]
    public void OnKeyDown_DifferentKeys_NotFiltered()
    {
        var buf = NewBuffer(out var published);

        Assert.True(buf.OnKeyDown(InputKeyPosition.KeyA));
        Assert.True(buf.OnKeyDown(InputKeyPosition.KeyB));
        Assert.True(buf.OnKeyDown(InputKeyPosition.KeyC));

        Assert.Equal(3, published.Items.Count);
    }

    [Fact]
    public void OnMouseButton_RecordsEvent()
    {
        var buf = NewBuffer(out var published);

        buf.OnMouseButton(1);
        buf.OnMouseButton(2);
        buf.OnMouseButton(3);

        var items = published.Items;
        Assert.Equal(3, items.Count);
        Assert.All(items, i => Assert.Equal(InputEventType.MouseButton, i.EventType));
    }

    [Fact]
    public void OnScroll_OneNotch_RecordsOneEvent()
    {
        var buf = NewBuffer(out var published);

        buf.OnScroll(InputEventBuffer.WheelDelta);  // 上滚一档

        var items = published.Items;
        Assert.Single(items);
        Assert.Equal(InputEventType.MouseScroll, items[0].EventType);
        Assert.Equal((short)1, items[0].Code);  // 上
    }

    [Fact]
    public void OnScroll_NegativeDelta_RecordsScrollDown()
    {
        var buf = NewBuffer(out var published);

        buf.OnScroll(-InputEventBuffer.WheelDelta);

        var items = published.Items;
        Assert.Single(items);
        Assert.Equal((short)2, items[0].Code);  // 下
    }

    [Fact]
    public void OnScroll_FractionalDeltas_AccumulateToWholeNotch()
    {
        var buf = NewBuffer(out var published);

        // 三次 40 凑成一档（120），第三次才记录
        buf.OnScroll(40);
        Assert.Empty(published.Items);
        buf.OnScroll(40);
        Assert.Empty(published.Items);
        buf.OnScroll(40);

        var items = published.Items;
        Assert.Single(items);
        Assert.Equal((short)1, items[0].Code);
    }

    [Fact]
    public void OnScroll_MultipleNotchesAtOnce_RecordsMultipleEvents()
    {
        var buf = NewBuffer(out var published);

        buf.OnScroll(InputEventBuffer.WheelDelta * 3);  // 一次滚三档

        var items = published.Items;
        Assert.Equal(3, items.Count);
        Assert.All(items, i => Assert.Equal((short)1, i.Code));
    }

    [Fact]
    public void OnScroll_RemainderCarriesOver()
    {
        var buf = NewBuffer(out var published);

        buf.OnScroll(200);  // 一档(120) + 余 80
        Assert.Single(published.Items);

        buf.OnScroll(40);   // 80 + 40 = 120 → 再一档
        Assert.Equal(2, published.Items.Count);
    }

    [Fact]
    public void ResetTransientState_AllowsHeldKeyAgain_AndDropsScrollRemainder()
    {
        var buf = NewBuffer(out var published);
        Assert.True(buf.OnKeyDown(InputKeyPosition.KeyA));
        buf.OnScroll(80);

        buf.ResetTransientState();

        Assert.True(buf.OnKeyDown(InputKeyPosition.KeyA));
        buf.OnScroll(40);
        var items = published.Items;
        Assert.Equal(2, items.Count(i => i.EventType == InputEventType.KeyDown));
        Assert.DoesNotContain(items, i => i.EventType == InputEventType.MouseScroll);
    }

    [Fact]
    public void Enqueue_GeneratesUniqueIds()
    {
        var buf = NewBuffer(out var published);

        buf.OnMouseButton(1);
        buf.OnMouseButton(1);

        var items = published.Items;
        Assert.NotEqual(items[0].Id, items[1].Id);
        Assert.NotEqual(Guid.Empty, items[0].Id);
    }

    [Fact]
    public void LegacyInputCache_SurvivesRestart_AndCommitsDrain()
    {
        var root = Path.Combine(Path.GetTempPath(), $"heartbeat-input-buffer-{Guid.NewGuid():N}");
        var path = Path.Combine(root, "input-event-facts-buffer.json");
        var item = new InputEventItem
        {
            Id = Guid.CreateVersion7(),
            EventType = InputEventType.KeyDown,
            CodeSet = InputCodeSets.HeartbeatKeyPositionV1,
            Code = (short)InputKeyPosition.KeyA,
            Timestamp = DateTimeOffset.UnixEpoch
        };
        try
        {
            using (var cache = new JsonFileCache<InputEventItem>(path, int.MaxValue,
                       HeartbeatCacheFormats.InputEventVersion2()))
                cache.Replace([item]);

            var restarted = new InputEventBuffer(new FakeClock(), durableProjectionPath: path);
            var drained = ((IUploadSource<InputEventItem>)restarted).ReadBatch();

            Assert.Equal(item.Id, Assert.Single(drained).Id);
            ((IUploadSource<InputEventItem>)restarted).Confirm(drained);
            var completed = new InputEventBuffer(new FakeClock(), durableProjectionPath: path);
            Assert.Empty(((IUploadSource<InputEventItem>)completed).ReadBatch());
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DurableUploadSource_DrainsAtMostFiveThousandItemsAndPreservesRemainder()
    {
        var root = Path.Combine(Path.GetTempPath(), $"heartbeat-input-buffer-{Guid.NewGuid():N}");
        var path = Path.Combine(root, "input-event-facts-buffer.json");
        var items = Enumerable.Range(0, 5_003).Select(index => new InputEventItem
        {
            Id = Guid.CreateVersion7(),
            EventType = InputEventType.KeyDown,
            CodeSet = InputCodeSets.HeartbeatKeyPositionV1,
            Code = (short)InputKeyPosition.KeyA,
            Timestamp = DateTimeOffset.UnixEpoch.AddTicks(index)
        }).ToArray();
        try
        {
            using (var cache = new JsonFileCache<InputEventItem>(path, int.MaxValue,
                       HeartbeatCacheFormats.InputEventVersion2()))
                cache.Replace(items.ToList());
            var buffer = new InputEventBuffer(new FakeClock(), durableProjectionPath: path);
            var source = (IUploadSource<InputEventItem>)buffer;

            var first = source.ReadBatch();

            Assert.Equal(5_000, first.Count);
            Assert.Equal(items.Take(5_000).Select(item => item.Id), first.Select(item => item.Id));
            Assert.True(
                JsonSerializer.SerializeToUtf8Bytes(
                    new InputEventUploadRequest { Events = first }).Length < 1_048_576,
                "The bounded InputEvent upload batch must stay below the default reverse-proxy body limit.");
            source.Confirm(first);
            Assert.Equal(new DeliveryRemainder(3, 0), source.Remainder);
            Assert.Equal(items.Skip(5_000).Select(item => item.Id), source.ReadBatch().Select(item => item.Id));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void LegacyCacheDiagnosticsClearAfterConfirmedDrain()
    {
        var root = Path.Combine(Path.GetTempPath(), $"heartbeat-input-buffer-{Guid.NewGuid():N}");
        var path = Path.Combine(root, "input-event-facts-buffer.json");
        try
        {
            using (var cache = new JsonFileCache<InputEventItem>(path, int.MaxValue,
                       HeartbeatCacheFormats.InputEventVersion2()))
                cache.Replace([new InputEventItem
                {
                    Id = Guid.CreateVersion7(), EventType = InputEventType.MouseButton,
                    CodeSet = InputCodeSets.HeartbeatKeyPositionV1, Code = 1,
                    Timestamp = DateTimeOffset.UnixEpoch
                }]);
            var registry = new UploadStatusRegistry();
            var buffer = new InputEventBuffer(new FakeClock(), durableProjectionPath: path, statusRegistry: registry);
            Assert.Equal(UploadStreamState.Backlog, registry.Snapshot[InputEventBuffer.StatusStreamName].State);

            buffer.Confirm(buffer.ReadAll());

            Assert.Equal(UploadStreamState.Ready, registry.Snapshot[InputEventBuffer.StatusStreamName].State);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
