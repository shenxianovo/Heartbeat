using Heartbeat.Recording;

namespace Heartbeat.Domain.Tests;

public sealed class TrackTests
{
    [Theory]
    [InlineData(TimeMode.Point, null)]
    [InlineData(TimeMode.Range, EndMode.Explicit)]
    [InlineData(TimeMode.Range, EndMode.NextRecord)]
    public void CreateAcceptsValidTimeMode(TimeMode timeMode, EndMode? endMode)
    {
        var track = Track.Create(
            Guid.NewGuid(),
            "activity.application.focus",
            1,
            timeMode,
            endMode,
            DateTimeOffset.UtcNow);

        Assert.Equal(timeMode, track.TimeMode);
        Assert.Equal(endMode, track.EndMode);
    }

    [Theory]
    [InlineData(TimeMode.Point, EndMode.Explicit)]
    [InlineData(TimeMode.Point, EndMode.NextRecord)]
    [InlineData(TimeMode.Range, null)]
    public void CreateRejectsInvalidTimeMode(TimeMode timeMode, EndMode? endMode)
    {
        Assert.Throws<ArgumentException>(() => Track.Create(
            Guid.NewGuid(),
            "activity.application.focus",
            1,
            timeMode,
            endMode,
            DateTimeOffset.UtcNow));
    }
}
