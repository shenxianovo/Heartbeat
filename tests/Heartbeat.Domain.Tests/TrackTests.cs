using Heartbeat.Recording;

namespace Heartbeat.Domain.Tests;

public sealed class TrackTests
{
    [Theory]
    [InlineData(TimeMode.Point)]
    [InlineData(TimeMode.Range)]
    public void CreateAcceptsValidTimeMode(TimeMode timeMode)
    {
        var track = Track.Create(
            Guid.NewGuid(),
            "activity.application.focus",
            1,
            timeMode,
            DateTimeOffset.UtcNow);

        Assert.Equal(timeMode, track.TimeMode);
    }

    [Fact]
    public void CreateRejectsInvalidTimeMode()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Track.Create(
            Guid.NewGuid(),
            "activity.application.focus",
            1,
            (TimeMode)42,
            DateTimeOffset.UtcNow));
    }
}
