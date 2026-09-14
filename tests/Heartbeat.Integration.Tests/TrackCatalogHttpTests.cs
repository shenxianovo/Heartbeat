using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Heartbeat.Integration.Tests;

[Collection(PostgresTestGroup.Name)]
public sealed class TrackCatalogHttpTests(PostgresFixture fixture) : PostgresTestBase(fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task OwnerListsOnlyOwnTracksWithCollectorDisplayInformationInStableOrder()
    {
        var ownerId = Guid.NewGuid();
        var otherOwnerId = Guid.NewGuid();
        await ProvisionTimelineAsync(ownerId);
        await ProvisionTimelineAsync(otherOwnerId);
        await using var factory = RecordingApiFactory.Create(ConnectionString, new FixedTimeProvider(Now));
        using var client = factory.CreateClient();
        var secondCollector = await RegisterAsync(
            client, ownerId, "heartbeat.collector.zeta", "device-z", "Zeta device");
        var firstCollector = await RegisterAsync(
            client, ownerId, "heartbeat.collector.alpha", "device-a", "Alpha device");
        var foreignCollector = await RegisterAsync(
            client, otherOwnerId, "heartbeat.collector.foreign", "device-f", "Foreign device");
        var secondTrack = await ResolveAsync(client, ownerId, secondCollector, "sample.zeta", 2);
        var firstTrack = await ResolveAsync(client, ownerId, firstCollector, "sample.alpha", 1);
        await ResolveAsync(client, otherOwnerId, foreignCollector, "sample.foreign", 1);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/tracks");
        request.Headers.Add(RecordingApiFactory.OwnerHeader, ownerId.ToString());
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var tracks = json.RootElement.GetProperty("tracks");
        Assert.Equal(2, tracks.GetArrayLength());
        Assert.Equal(firstTrack, tracks[0].GetProperty("id").GetGuid());
        Assert.Equal(firstCollector, tracks[0].GetProperty("collectorId").GetGuid());
        Assert.Equal("heartbeat.collector.alpha", tracks[0].GetProperty("collectorKey").GetString());
        Assert.Equal("device-a", tracks[0].GetProperty("collectorTarget").GetString());
        Assert.Equal("Alpha device", tracks[0].GetProperty("collectorDisplayName").GetString());
        Assert.Equal("sample.alpha", tracks[0].GetProperty("type").GetString());
        Assert.Equal(1, tracks[0].GetProperty("version").GetInt32());
        Assert.Equal("range", tracks[0].GetProperty("timeMode").GetString());
        Assert.Equal("explicit", tracks[0].GetProperty("endMode").GetString());
        Assert.Equal(Now, tracks[0].GetProperty("createdAt").GetDateTimeOffset());
        Assert.Equal(secondTrack, tracks[1].GetProperty("id").GetGuid());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnerWithoutTimelineOrTracksGetsEmptyCatalog(bool provisionTimeline)
    {
        var ownerId = Guid.NewGuid();
        if (provisionTimeline)
        {
            await ProvisionTimelineAsync(ownerId);
        }

        await using var factory = RecordingApiFactory.Create(ConnectionString, new FixedTimeProvider(Now));
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/tracks");
        request.Headers.Add(RecordingApiFactory.OwnerHeader, ownerId.ToString());

        using var response = await client.SendAsync(request);

        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Empty(json.RootElement.GetProperty("tracks").EnumerateArray());
    }

    [Fact]
    public async Task AnonymousCallerCannotListTracks()
    {
        await using var factory = RecordingApiFactory.Create(ConnectionString, new FixedTimeProvider(Now));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/v1/tracks");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static async Task<Guid> RegisterAsync(
        HttpClient client,
        Guid ownerId,
        string key,
        string target,
        string displayName)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/collectors")
        {
            Content = JsonContent.Create(new { key, target, displayName }),
        };
        request.Headers.Add(RecordingApiFactory.OwnerHeader, ownerId.ToString());
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<Guid> ResolveAsync(
        HttpClient client,
        Guid ownerId,
        Guid collectorId,
        string type,
        int version)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/collectors/{collectorId}/tracks")
        {
            Content = JsonContent.Create(new { type, version, timeMode = "range", endMode = "explicit" }),
        };
        request.Headers.Add(RecordingApiFactory.OwnerHeader, ownerId.ToString());
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("id").GetGuid();
    }
}
