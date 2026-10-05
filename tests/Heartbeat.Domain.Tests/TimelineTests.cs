using Heartbeat.Recording;

namespace Heartbeat.Domain.Tests;

public sealed class TimelineTests
{
    [Fact]
    public void CreateNormalizesValues()
    {
        var ownerId = Guid.NewGuid();
        var createdAt = new DateTimeOffset(2026, 9, 12, 14, 0, 0, TimeSpan.FromHours(8));

        var timeline = Timeline.Create(ownerId, "  My Timeline  ", createdAt);

        Assert.Equal(7, timeline.Id.Version);
        Assert.Equal(ownerId, timeline.OwnerId);
        Assert.Equal("My Timeline", timeline.DisplayName);
        Assert.Equal(TimeSpan.Zero, timeline.CreatedAt.Offset);
    }

    [Fact]
    public void UpdateDisplayNameRejectsWhitespace()
    {
        var timeline = Timeline.Create(Guid.NewGuid(), "My Timeline", DateTimeOffset.UtcNow);

        Assert.Throws<ArgumentException>(() => timeline.UpdateDisplayName("  "));
    }
}
