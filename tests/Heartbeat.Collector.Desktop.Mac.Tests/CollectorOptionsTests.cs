using Heartbeat.Collector.Desktop.Mac;

namespace Heartbeat.Collector.Desktop.Mac.Tests;

public sealed class CollectorOptionsTests
{
    [Fact]
    public void EnvironmentProvidesRequiredOptions()
    {
        var options = CollectorOptions.Parse([], new Dictionary<string, string?>
        {
            ["HEARTBEAT_API_BASE_URL"] = "http://localhost:8080",
            ["HEARTBEAT_AUTH_TOKEN"] = "token",
            ["HEARTBEAT_COLLECTOR_TARGET"] = "device-a",
            ["HEARTBEAT_COLLECTOR_DISPLAY_NAME"] = "My Mac",
            ["HEARTBEAT_COLLECTOR_INTERVAL_SECONDS"] = "2",
            ["HEARTBEAT_COLLECTOR_ONCE"] = "true",
        });

        Assert.Equal(new Uri("http://localhost:8080/"), options.ApiBaseUrl);
        Assert.Equal("token", options.AuthToken);
        Assert.Equal("device-a", options.Target);
        Assert.Equal("My Mac", options.DisplayName);
        Assert.Equal(TimeSpan.FromSeconds(2), options.Interval);
        Assert.True(options.Once);
    }

    [Fact]
    public void ArgumentsOverrideEnvironment()
    {
        var options = CollectorOptions.Parse([
            "--api", "http://127.0.0.1:8080",
            "--token", "arg-token",
            "--target", "device-b",
            "--display-name", "Desk",
            "--interval-seconds", "3",
            "--once",
        ], new Dictionary<string, string?>
        {
            ["HEARTBEAT_API_BASE_URL"] = "http://localhost:8080",
            ["HEARTBEAT_AUTH_TOKEN"] = "env-token",
            ["HEARTBEAT_COLLECTOR_TARGET"] = "device-a",
        });

        Assert.Equal(new Uri("http://127.0.0.1:8080/"), options.ApiBaseUrl);
        Assert.Equal("arg-token", options.AuthToken);
        Assert.Equal("device-b", options.Target);
        Assert.Equal("Desk", options.DisplayName);
        Assert.Equal(TimeSpan.FromSeconds(3), options.Interval);
        Assert.True(options.Once);
    }

    [Fact]
    public void TargetIsRequiredBecauseItIsTheCollectorBinding()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            CollectorOptions.Parse([], new Dictionary<string, string?>
            {
                ["HEARTBEAT_API_BASE_URL"] = "http://localhost:8080",
                ["HEARTBEAT_AUTH_TOKEN"] = "token",
            }));

        Assert.Contains("HEARTBEAT_COLLECTOR_TARGET", exception.Message, StringComparison.Ordinal);
    }
}
