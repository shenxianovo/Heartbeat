using Heartbeat.Hub;

namespace Heartbeat.Collector.Desktop.Mac.Tests;

public sealed class ContinuousObservationClockTests
{
    [Fact]
    public void SleepDoesNotMoveResumedApplicationAwayOrInputRecordsAnHourIntoThePast()
    {
        var start = new DateTimeOffset(2026, 9, 14, 8, 0, 0, TimeSpan.Zero);
        var provider = new AdjustableTimeProvider(start);
        var clock = new ContinuousObservationClock(new MacContinuousTimeProvider(provider, provider.ReadNativeNanoseconds));
        var records = new List<(string Type, RecordSnapshot Record)>();
        var projector = new DesktopRecordProjector("heartbeat.collector.desktop.macos", "mac", "Mac", TimeSpan.FromSeconds(10), TimeSpan.Zero,
            (route, record) => records.Add((route.Track.Type!, record)));
        var application = new DesktopActivitySample(
            new ForegroundApplication("macos", "bundle_id", "com.example.App", "Example"), "Document");
        projector.Apply(new DesktopObservation.Activity(application), clock.GetUtcNow());
        projector.Apply(new DesktopObservation.AwayEntered(DesktopAwayReason.SystemSleep), clock.GetUtcNow());

        // macOS uptime pauses during sleep while UTC and its continuous clock advance.
        provider.UtcNow = start.AddHours(1);
        provider.ContinuousSeconds += 3600;
        // The first resumed input can precede the wake notification; no wake rebase is required.
        projector.Apply(new DesktopObservation.Input(new DesktopInputObservation(DesktopInputKind.KeyDown, 12)), clock.GetUtcNow());
        projector.Apply(new DesktopObservation.AwayExited(DesktopAwayReason.SystemSleep, application), clock.GetUtcNow());

        Assert.Equal(start.AddHours(1), records.Last(item => item.Type == "desktop.application.foreground").Record.StartedAt);
        Assert.Equal(start.AddHours(1), records.Last(item => item.Type == "desktop.system.away").Record.EndedAt);
        Assert.Equal(start.AddHours(1), records.Last(item => item.Type == "desktop.input.event").Record.StartedAt);
    }

    [Theory]
    [InlineData(-3)]
    [InlineData(3)]
    public void UsesMonotonicElapsedTimeAfterEstablishingAbsoluteBaseline(int wallClockJumpHours)
    {
        var provider = new AdjustableTimeProvider(
            new DateTimeOffset(2026, 9, 14, 8, 0, 0, TimeSpan.Zero));
        var clock = new ContinuousObservationClock(new MacContinuousTimeProvider(provider, provider.ReadNativeNanoseconds));

        provider.UtcNow = provider.UtcNow.AddHours(wallClockJumpHours);
        provider.Timestamp += 7;
        provider.ContinuousSeconds += 7;

        Assert.Equal(
            new DateTimeOffset(2026, 9, 14, 8, 0, 7, TimeSpan.Zero),
            clock.GetUtcNow());
    }

    [Fact]
    public void NativeContinuousClockCanBeReadWithoutObservingTheDesktop()
    {
        if (!OperatingSystem.IsMacOS()) return;

        var provider = new MacContinuousTimeProvider();
        var before = provider.GetTimestamp();
        var after = provider.GetTimestamp();

        Assert.True(before > 0);
        Assert.True(after >= before);
        Assert.Equal(1_000_000_000, provider.TimestampFrequency);
    }

    private sealed class AdjustableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
        public long Timestamp { get; set; }
        public long ContinuousSeconds { get; set; }
        public override long TimestampFrequency => 1;
        public override DateTimeOffset GetUtcNow() => UtcNow;
        public override long GetTimestamp() => Timestamp;

        public ulong ReadNativeNanoseconds(int clockId) => clockId switch
        {
            4 => checked((ulong)ContinuousSeconds * 1_000_000_000),
            8 => checked((ulong)Timestamp * 1_000_000_000),
            _ => throw new ArgumentOutOfRangeException(nameof(clockId)),
        };
    }
}
