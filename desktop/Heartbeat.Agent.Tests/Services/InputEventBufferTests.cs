using Heartbeat.Agent.Services;
using Heartbeat.Agent.Utils;
using Heartbeat.Core.DTOs.Input;

namespace Heartbeat.Agent.Tests.Services;

public class InputEventBufferTests
{
    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UnixEpoch;
    }

    private static InputEventBuffer NewBuffer(int capacity = 100_000)
        => new(new FakeClock(), capacity);

    [Fact]
    public void OnKeyDown_RecordsEvent()
    {
        var buf = NewBuffer();

        Assert.True(buf.OnKeyDown(65));

        var items = buf.DrainAll();
        Assert.Single(items);
        Assert.Equal(InputEventType.KeyDown, items[0].EventType);
        Assert.Equal((short)65, items[0].Code);
    }

    [Fact]
    public void OnKeyDown_FiltersAutoRepeat_UntilKeyUp()
    {
        var buf = NewBuffer();

        Assert.True(buf.OnKeyDown(65));   // 首次记录
        Assert.False(buf.OnKeyDown(65));  // 自动重复，丢弃
        Assert.False(buf.OnKeyDown(65));  // 仍丢弃

        buf.OnKeyUp(65);
        Assert.True(buf.OnKeyDown(65));   // 抬起后再按，重新记录

        Assert.Equal(2, buf.DrainAll().Count);
    }

    [Fact]
    public void OnKeyDown_DifferentKeys_NotFiltered()
    {
        var buf = NewBuffer();

        Assert.True(buf.OnKeyDown(65));
        Assert.True(buf.OnKeyDown(66));
        Assert.True(buf.OnKeyDown(67));

        Assert.Equal(3, buf.DrainAll().Count);
    }

    [Fact]
    public void OnMouseButton_RecordsEvent()
    {
        var buf = NewBuffer();

        buf.OnMouseButton(1);
        buf.OnMouseButton(2);
        buf.OnMouseButton(3);

        var items = buf.DrainAll();
        Assert.Equal(3, items.Count);
        Assert.All(items, i => Assert.Equal(InputEventType.MouseButton, i.EventType));
    }

    [Fact]
    public void OnScroll_OneNotch_RecordsOneEvent()
    {
        var buf = NewBuffer();

        buf.OnScroll(InputEventBuffer.WheelDelta);  // 上滚一档

        var items = buf.DrainAll();
        Assert.Single(items);
        Assert.Equal(InputEventType.MouseScroll, items[0].EventType);
        Assert.Equal((short)1, items[0].Code);  // 上
    }

    [Fact]
    public void OnScroll_NegativeDelta_RecordsScrollDown()
    {
        var buf = NewBuffer();

        buf.OnScroll(-InputEventBuffer.WheelDelta);

        var items = buf.DrainAll();
        Assert.Single(items);
        Assert.Equal((short)2, items[0].Code);  // 下
    }

    [Fact]
    public void OnScroll_FractionalDeltas_AccumulateToWholeNotch()
    {
        var buf = NewBuffer();

        // 三次 40 凑成一档（120），第三次才记录
        buf.OnScroll(40);
        Assert.Empty(buf.DrainAll());
        buf.OnScroll(40);
        Assert.Empty(buf.DrainAll());
        buf.OnScroll(40);

        var items = buf.DrainAll();
        Assert.Single(items);
        Assert.Equal((short)1, items[0].Code);
    }

    [Fact]
    public void OnScroll_MultipleNotchesAtOnce_RecordsMultipleEvents()
    {
        var buf = NewBuffer();

        buf.OnScroll(InputEventBuffer.WheelDelta * 3);  // 一次滚三档

        var items = buf.DrainAll();
        Assert.Equal(3, items.Count);
        Assert.All(items, i => Assert.Equal((short)1, i.Code));
    }

    [Fact]
    public void OnScroll_RemainderCarriesOver()
    {
        var buf = NewBuffer();

        buf.OnScroll(200);  // 一档(120) + 余 80
        Assert.Single(buf.DrainAll());

        buf.OnScroll(40);   // 80 + 40 = 120 → 再一档
        Assert.Single(buf.DrainAll());
    }

    [Fact]
    public void Enqueue_RespectsCapacity_DropsOldest()
    {
        var buf = NewBuffer(capacity: 3);

        buf.OnMouseButton(1);
        buf.OnMouseButton(2);
        buf.OnMouseButton(3);
        buf.OnMouseButton(1);  // 超出容量，丢弃最旧

        Assert.Equal(3, buf.Count);
        Assert.Equal(3, buf.DrainAll().Count);
    }

    [Fact]
    public void DrainAll_EmptiesBuffer()
    {
        var buf = NewBuffer();

        buf.OnKeyDown(65);
        Assert.Single(buf.DrainAll());
        Assert.Empty(buf.DrainAll());
        Assert.Equal(0, buf.Count);
    }

    [Fact]
    public void Enqueue_GeneratesUniqueIds()
    {
        var buf = NewBuffer();

        buf.OnMouseButton(1);
        buf.OnMouseButton(1);

        var items = buf.DrainAll();
        Assert.NotEqual(items[0].Id, items[1].Id);
        Assert.NotEqual(Guid.Empty, items[0].Id);
    }
}
