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
        Assert.Equal(TimeSpan.FromSeconds(6), options.MaximumConfirmationGap);
        Assert.Equal(TimeSpan.FromMilliseconds(1500), options.WindowTitleDwell);
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
            "--maximum-gap-seconds", "9",
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
        Assert.Equal(TimeSpan.FromSeconds(9), options.MaximumConfirmationGap);
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

    [Fact]
    public void MaximumGapMustExceedSamplingInterval()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CollectorOptions.Parse(
            [
                "--hub", "http://127.0.0.1:4318",
                "--hub-token", "local",
                "--target", "device-a",
                "--interval-seconds", "5",
                "--maximum-gap-seconds", "5",
            ],
            new Dictionary<string, string?>()));
    }

    [Fact]
    public void WindowTitleDwellIsConfigurableInMilliseconds()
    {
        var options = CollectorOptions.Parse([
            "--hub", "http://localhost:4318",
            "--hub-token", "arg-token",
            "--target", "device-b",
            "--display-name", "Desk",
            "--window-title-dwell-ms", "800",
        ], new Dictionary<string, string?>());

        Assert.Equal(TimeSpan.FromMilliseconds(800), options.WindowTitleDwell);
    }

    [Fact]
    public void WindowTitleDwellCannotOutlastTheConfirmationGap()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => CollectorOptions.Parse([
            "--hub", "http://localhost:4318",
            "--hub-token", "arg-token",
            "--target", "device-b",
            "--display-name", "Desk",
            "--maximum-gap-seconds", "9",
            "--window-title-dwell-ms", "9000",
        ], new Dictionary<string, string?>()));

        Assert.Contains("confirmation gap", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowTitleDwellRejectsNegativeValues()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => CollectorOptions.Parse([
            "--hub", "http://localhost:4318",
            "--hub-token", "arg-token",
            "--target", "device-b",
            "--display-name", "Desk",
            "--window-title-dwell-ms", "-1",
        ], new Dictionary<string, string?>()));

        Assert.Contains("negative", error.Message, StringComparison.Ordinal);
    }
}
