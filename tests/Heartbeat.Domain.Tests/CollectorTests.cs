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

    [Fact]
    public void CreateAcceptsValuesAtMaximumLength()
    {
        var collector = Collector.Create(
            Guid.NewGuid(),
            $"heartbeat.{new string('a', 245)}",
            new string('t', Collector.MaximumTextLength),
            new string('n', Collector.MaximumTextLength),
            DateTimeOffset.UtcNow);

        Assert.Equal(Collector.MaximumTextLength, collector.Key.Length);
        Assert.Equal(Collector.MaximumTextLength, collector.Target.Length);
        Assert.Equal(Collector.MaximumTextLength, collector.DisplayName.Length);
    }

    [Theory]
    [InlineData("key")]
    [InlineData("target")]
    [InlineData("displayName")]
    public void CreateRejectsValuesOverMaximumLength(string parameterName)
    {
        var longValue = new string('a', Collector.MaximumTextLength + 1);

        var error = Assert.Throws<ArgumentException>(() => Collector.Create(
            Guid.NewGuid(),
            parameterName == "key" ? $"heartbeat.{new string('a', 246)}" : "heartbeat.collector.desktop.macos",
            parameterName == "target" ? longValue : "device-1",
            parameterName == "displayName" ? longValue : "My Mac",
            DateTimeOffset.UtcNow));

        Assert.Equal(parameterName, error.ParamName);
    }
}
