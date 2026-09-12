using Heartbeat.Recording;

namespace Heartbeat.Domain.Tests;

public sealed class CollectorTests
{
    [Fact]
    public void CreateNormalizesValues()
    {
        var collector = Collector.Create(
            Guid.NewGuid(),
            "heartbeat.collector.desktop.macos",
            "  device-1  ",
            "  My Mac  ",
            DateTimeOffset.UtcNow);

        Assert.Equal("heartbeat.collector.desktop.macos", collector.Key);
        Assert.Equal("device-1", collector.Target);
        Assert.Equal("My Mac", collector.DisplayName);
    }

    [Theory]
    [InlineData("Desktop")]
    [InlineData("heartbeat..desktop")]
    [InlineData("heartbeat.collector.MacOS")]
    public void CreateRejectsInvalidKey(string key)
    {
        Assert.Throws<ArgumentException>(() => Collector.Create(
            Guid.NewGuid(),
            key,
            "device-1",
            "My Mac",
            DateTimeOffset.UtcNow));
    }
}
