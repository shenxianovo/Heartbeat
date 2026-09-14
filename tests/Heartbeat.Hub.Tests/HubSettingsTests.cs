using Heartbeat.Hub.Host;
using Microsoft.Extensions.Configuration;

namespace Heartbeat.Hub.Tests;

public sealed class HubSettingsTests
{
    [Fact]
    public void AuthOriginHasTheProductionDefault()
    {
        using var fixture = new QueueFixture();
        var values = Values(fixture);
        var settings = HubSettings.Read(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        Assert.Equal(new Uri("https://auth.shenxianovo.com"), settings.AuthUrl);
    }

    [Fact]
    public void ApiKeyIsRequired()
    {
        using var fixture = new QueueFixture();
        var values = Values(fixture);
        values.Remove("Hub:ApiKey");
        Assert.Throws<ArgumentException>(() =>
            HubSettings.Read(new ConfigurationBuilder().AddInMemoryCollection(values).Build()));
    }

    private static Dictionary<string, string?> Values(QueueFixture fixture) => new()
    {
        ["Hub:DatabasePath"] = fixture.DatabasePath,
        ["Hub:BackendUrl"] = fixture.Destination.BackendUrl.AbsoluteUri,
        ["Hub:OwnerId"] = fixture.Destination.OwnerId.ToString(),
        ["Hub:ApiKey"] = "test-api-key",
        ["Hub:AccessToken"] = "local-test-secret-with-at-least-32-characters",
    };
}
