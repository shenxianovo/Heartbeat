using Heartbeat.Collector.Desktop.Mac;

namespace Heartbeat.Collector.Desktop.Mac.Tests;

public sealed class ForegroundRecordBatcherTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 12, 16, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SameApplicationExtendsTheSameRecord()
    {
        var time = new MutableTimeProvider(Start);
        var batcher = new ForegroundRecordBatcher(time);
        var application = new ForegroundApplication("macos", "bundle_id", "com.example.App");

        var first = batcher.Confirm(application);
        time.Advance(TimeSpan.FromSeconds(5));
        var second = batcher.Confirm(application);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(Start, second.StartedAt);
        Assert.Equal(Start.AddSeconds(5), second.EndedAt);
    }

    [Fact]
    public void ApplicationChangeStartsANewRecord()
    {
        var time = new MutableTimeProvider(Start);
        var batcher = new ForegroundRecordBatcher(time);

        var first = batcher.Confirm(new ForegroundApplication("macos", "bundle_id", "com.example.First"));
        time.Advance(TimeSpan.FromSeconds(5));
        var second = batcher.Confirm(new ForegroundApplication("macos", "bundle_id", "com.example.Second"));

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(Start.AddSeconds(5), second.StartedAt);
        Assert.Equal(Start.AddSeconds(5), second.EndedAt);
    }

    [Theory]
    [InlineData(3600)]
    [InlineData(-3600)]
    public void SystemClockChangesDoNotChangeElapsedObservationTime(int clockShiftSeconds)
    {
        var time = new MutableTimeProvider(Start);
        var batcher = new ForegroundRecordBatcher(time);
        var application = new ForegroundApplication("macos", "bundle_id", "com.example.App");

        var first = batcher.Confirm(application);
        time.Advance(TimeSpan.FromSeconds(5));
        time.ShiftSystemClock(TimeSpan.FromSeconds(clockShiftSeconds));
        var second = batcher.Confirm(application);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(Start, second.StartedAt);
        Assert.Equal(Start.AddSeconds(5), second.EndedAt);
    }

    [Fact]
    public void UnknownForegroundApplicationBreaksContinuity()
    {
        var time = new MutableTimeProvider(Start);
        var batcher = new ForegroundRecordBatcher(time);
        var application = new ForegroundApplication("macos", "bundle_id", "com.example.App");

        var first = batcher.Confirm(application);
        Assert.Null(batcher.Confirm(null));
        time.Advance(TimeSpan.FromSeconds(5));
        var second = batcher.Confirm(application);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(Start.AddSeconds(5), second.StartedAt);
    }

    [Fact]
    public void ApplicationChangesKeepTheContinuousSamplingTimeBase()
    {
        var time = new MutableTimeProvider(Start);
        var batcher = new ForegroundRecordBatcher(time);
        var first = batcher.Confirm(new ForegroundApplication("macos", "bundle_id", "com.example.First"));

        time.Advance(TimeSpan.FromSeconds(5));
        time.ShiftSystemClock(TimeSpan.FromHours(1));
        var second = batcher.Confirm(new ForegroundApplication("macos", "bundle_id", "com.example.Second"));

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(Start.AddSeconds(5), second.StartedAt);
        Assert.Equal(second.StartedAt, second.EndedAt);
    }

    [Fact]
    public void SamplingResumesWithANewSystemTimeBaseAfterUnknownApplication()
    {
        var time = new MutableTimeProvider(Start);
        var batcher = new ForegroundRecordBatcher(time);
        var application = new ForegroundApplication("macos", "bundle_id", "com.example.App");
        var first = batcher.Confirm(application);
        Assert.Null(batcher.Confirm(null));

        time.Advance(TimeSpan.FromSeconds(5));
        time.ShiftSystemClock(TimeSpan.FromHours(1));
        var second = batcher.Confirm(application);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(Start.AddHours(1).AddSeconds(5), second.StartedAt);
        Assert.Equal(second.StartedAt, second.EndedAt);
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        private long _timestamp;

        public override DateTimeOffset GetUtcNow() => _now;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan interval)
        {
            _now += interval;
            _timestamp += interval.Ticks;
        }

        public void ShiftSystemClock(TimeSpan offset) => _now += offset;
    }
}
