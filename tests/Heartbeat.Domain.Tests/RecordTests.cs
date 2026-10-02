using System.Text.Json;
using Heartbeat.Recording;
using RecordingRecord = Heartbeat.Recording.Record;

namespace Heartbeat.Domain.Tests;

public sealed class RecordTests
{
    [Fact]
    public void CreateStoresObservedAtOnlyWhenDifferentFromTimelineTime()
    {
        var startedAt = new DateTimeOffset(2026, 9, 12, 14, 0, 0, TimeSpan.FromHours(8));
        using var document = JsonDocument.Parse("""{"application":"code"}""");

        var record = RecordingRecord.Create(
            Guid.CreateVersion7(),
            CreateTrack(TimeMode.Point),
            startedAt,
            null,
            startedAt.ToUniversalTime(),
            startedAt.AddMinutes(1),
            document.RootElement);

        Assert.Null(record.ObservedAt);
        Assert.Equal(TimeSpan.Zero, record.StartedAt.Offset);
        Assert.Equal(TimeSpan.Zero, record.ReceivedAt.Offset);
        Assert.Equal("code", record.Value.GetProperty("application").GetString());
    }

    [Fact]
    public void CreatePreservesDifferentObservedAt()
    {
        var startedAt = DateTimeOffset.UtcNow;
        var observedAt = startedAt.AddMinutes(1);

        var record = RecordingRecord.Create(
            Guid.CreateVersion7(),
            CreateTrack(TimeMode.Point),
            startedAt,
            null,
            observedAt,
            observedAt,
            JsonSerializer.SerializeToElement(42));

        Assert.Equal(observedAt, record.ObservedAt);
    }

    [Fact]
    public void CreateRejectsEndBeforeStart()
    {
        var startedAt = DateTimeOffset.UtcNow;

        Assert.Throws<ArgumentException>(() => RecordingRecord.Create(
            Guid.CreateVersion7(),
            CreateTrack(TimeMode.Range),
            startedAt,
            startedAt.AddTicks(-1),
            null,
            startedAt,
            JsonSerializer.SerializeToElement(new { })));
    }

    [Fact]
    public void CreateRejectsIdThatIsNotVersionSeven()
    {
        Assert.Throws<ArgumentException>(() => RecordingRecord.Create(
            Guid.NewGuid(),
            CreateTrack(TimeMode.Point),
            DateTimeOffset.UtcNow,
            null,
            null,
            DateTimeOffset.UtcNow,
            JsonSerializer.SerializeToElement(new { })));
    }

    [Theory]
    [InlineData(TimeMode.Point, true, false)]
    [InlineData(TimeMode.Point, false, true)]
    [InlineData(TimeMode.Range, true, true)]
    [InlineData(TimeMode.Range, false, false)]
    public void CreateEnforcesTrackTimeMode(TimeMode timeMode, bool hasEnd, bool isValid)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var track = CreateTrack(timeMode);

        void Act() => RecordingRecord.Create(
            Guid.CreateVersion7(),
            track,
            startedAt,
            hasEnd ? startedAt.AddMinutes(1) : null,
            null,
            startedAt,
            JsonSerializer.SerializeToElement(new { }));

        if (isValid)
        {
            Act();
        }
        else
        {
            Assert.Throws<ArgumentException>(Act);
        }
    }

    private static Track CreateTrack(TimeMode timeMode) => Track.Create(
        Guid.NewGuid(),
        "activity.application.focus",
        1,
        timeMode,
        DateTimeOffset.UtcNow);
}
