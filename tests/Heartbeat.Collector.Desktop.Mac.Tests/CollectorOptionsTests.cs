using Heartbeat.Collector.Desktop.Mac;

namespace Heartbeat.Collector.Desktop.Mac.Tests;

public sealed class CollectorOptionsTests
{
    [Fact]
    public void EnvironmentProvidesHubAndCollectorOptionsWithoutBackendIdentity()
    {
        var options = CollectorOptions.Parse([], new Dictionary<string, string?>
        {
            ["HEARTBEAT_HUB_URL"] = "http://127.0.0.1:4318",
            ["HEARTBEAT_HUB_TOKEN"] = "local-token",
            ["HEARTBEAT_COLLECTOR_TARGET"] = "device-a",
            ["HEARTBEAT_COLLECTOR_DISPLAY_NAME"] = "My Mac",
            ["HEARTBEAT_COLLECTOR_INTERVAL_SECONDS"] = "2",
            ["HEARTBEAT_COLLECTOR_ONCE"] = "true",
        });

        Assert.Equal(new Uri("http://127.0.0.1:4318/"), options.HubBaseUrl);
        Assert.Equal("local-token", options.HubToken);
        Assert.Equal("device-a", options.Target);
        Assert.Equal("My Mac", options.DisplayName);
        Assert.Equal(TimeSpan.FromSeconds(2), options.Interval);
        Assert.True(options.Once);
    }

    [Fact]
    public void ArgumentsOverrideEnvironment()
    {
        var options = CollectorOptions.Parse([
            "--hub", "http://localhost:4318",
            "--hub-token", "arg-token",
            "--target", "device-b",
            "--display-name", "Desk",
            "--interval-seconds", "3",
            "--once",
        ], new Dictionary<string, string?>
        {
            ["HEARTBEAT_HUB_URL"] = "http://127.0.0.1:4318",
            ["HEARTBEAT_HUB_TOKEN"] = "env-token",
            ["HEARTBEAT_COLLECTOR_TARGET"] = "device-a",
        });

        Assert.Equal(new Uri("http://localhost:4318/"), options.HubBaseUrl);
        Assert.Equal("arg-token", options.HubToken);
        Assert.Equal("device-b", options.Target);
        Assert.Equal("Desk", options.DisplayName);
        Assert.Equal(TimeSpan.FromSeconds(3), options.Interval);
        Assert.True(options.Once);
    }

    [Theory]
    [InlineData("--api")]
    [InlineData("--token")]
    [InlineData("--track")]
    public void OldBackendAndTrackOptionsAreNotPartOfTheCollectorInterface(string option)
    {
        Assert.Throws<ArgumentException>(() => CollectorOptions.Parse([
            option, "old-value", "--hub", "http://127.0.0.1:4318",
            "--hub-token", "local", "--target", "device-a",
        ], new Dictionary<string, string?>()));
    }

    [Fact]
    public void HubTokenAndTargetAreRequired()
    {
        Assert.Throws<ArgumentException>(() => CollectorOptions.Parse([], new Dictionary<string, string?>
        {
            ["HEARTBEAT_HUB_URL"] = "http://127.0.0.1:4318",
            ["HEARTBEAT_HUB_TOKEN"] = "local-token",
        }));
    }
}
