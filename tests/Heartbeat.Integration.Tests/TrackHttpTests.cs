using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Heartbeat.Recording;
using Heartbeat.Recording.Protocols;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Integration.Tests;

[Collection(PostgresTestGroup.Name)]
public sealed class TrackHttpTests(PostgresFixture fixture) : PostgresTestBase(fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task OwnerCanResolveStableTrackAfterRegisteringCollector()
    {
        var ownerId = Guid.NewGuid();
        await ProvisionTimelineAsync(ownerId);
        await using var factory = RecordingApiFactory.Create(ConnectionString, new FixedTimeProvider(Now));
        using var client = factory.CreateClient();
        var collectorId = await RegisterAsync(client, ownerId);

        using var first = await ResolveAsync(client, ownerId, collectorId);
        using var second = await ResolveAsync(client, ownerId, collectorId);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        using var json = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        using var repeated = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        var track = json.RootElement;
        Assert.Equal(7, track.GetProperty("id").GetGuid().Version);
        Assert.Equal(track.GetProperty("id").GetGuid(), repeated.RootElement.GetProperty("id").GetGuid());
        Assert.Equal(collectorId, track.GetProperty("collectorId").GetGuid());
        Assert.Equal("desktop.application.foreground", track.GetProperty("type").GetString());
        Assert.Equal(1, track.GetProperty("version").GetInt32());
        Assert.Equal("range", track.GetProperty("timeMode").GetString());
        Assert.Equal("explicit", track.GetProperty("endMode").GetString());
        Assert.Equal(Now, track.GetProperty("createdAt").GetDateTimeOffset());
        await using var db = CreateDbContext();
        Assert.Single(await db.Tracks.ToListAsync());
    }

    [Fact]
    public async Task ConcurrentResolutionsReturnSameTrack()
    {
        var ownerId = Guid.NewGuid();
        await ProvisionTimelineAsync(ownerId);
        await using var factory = RecordingApiFactory.Create(ConnectionString, new FixedTimeProvider(Now));
        using var client = factory.CreateClient();
        var collectorId = await RegisterAsync(client, ownerId);

        var responses = await Task.WhenAll(Enumerable.Range(0, 6)
            .Select(_ => ResolveAsync(client, ownerId, collectorId)));
        var ids = new List<Guid>();
        foreach (var response in responses)
        {
            using (response)
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                ids.Add(json.RootElement.GetProperty("id").GetGuid());
            }
        }

        Assert.Single(ids.Distinct());
        await using var db = CreateDbContext();
        Assert.Single(await db.Tracks.ToListAsync());
    }

    [Fact]
    public async Task DifferentCollectorsCanUseSameProtocol()
    {
        var ownerId = Guid.NewGuid();
        await ProvisionTimelineAsync(ownerId);
        await using var factory = RecordingApiFactory.Create(ConnectionString, new FixedTimeProvider(Now));
        using var client = factory.CreateClient();
        var firstId = await RegisterAsync(client, ownerId);
        var secondId = await RegisterAsync(client, ownerId, "heartbeat.collector.other");

        using var first = await ResolveAsync(client, ownerId, firstId);
        using var second = await ResolveAsync(client, ownerId, secondId);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        await using var db = CreateDbContext();
        Assert.Equal(2, await db.Tracks.CountAsync());
    }

    [Fact]
    public async Task OtherOwnersCollectorAndMissingCollectorHaveSameResponse()
    {
        var ownerId = Guid.NewGuid();
        var otherOwnerId = Guid.NewGuid();
        await ProvisionTimelineAsync(ownerId);
        await ProvisionTimelineAsync(otherOwnerId);
        await using var factory = RecordingApiFactory.Create(ConnectionString, new FixedTimeProvider(Now));
        using var client = factory.CreateClient();
        var collectorId = await RegisterAsync(client, ownerId);

        using var forbidden = await ResolveAsync(client, otherOwnerId, collectorId);
        using var missing = await ResolveAsync(client, otherOwnerId, Guid.NewGuid());

        await AssertProblemAsync(forbidden, HttpStatusCode.NotFound, "collector_not_found");
        await AssertProblemAsync(missing, HttpStatusCode.NotFound, "collector_not_found");
        await using var db = CreateDbContext();
        Assert.Empty(await db.Tracks.ToListAsync());
    }

    [Theory]
    [InlineData("{\"type\":\"unknown\",\"version\":1}", "unsupported_protocol")]
    [InlineData("{\"type\":\"desktop.application.foreground\",\"version\":2}", "unsupported_protocol")]
    [InlineData("{\"type\":\" \",\"version\":1}", "invalid_request")]
    [InlineData("{\"type\":\"desktop.application.foreground\",\"version\":0}", "invalid_request")]
    [InlineData("{}", "invalid_request")]
    public async Task InvalidOrUnsupportedProtocolDoesNotCreateTrack(string body, string code)
    {
        var ownerId = Guid.NewGuid();
        await ProvisionTimelineAsync(ownerId);
        await using var factory = RecordingApiFactory.Create(ConnectionString, new FixedTimeProvider(Now));
        using var client = factory.CreateClient();
        var collectorId = await RegisterAsync(client, ownerId);

        using var response = await ResolveAsync(client, ownerId, collectorId, body);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, code);
        await using var db = CreateDbContext();
        Assert.Empty(await db.Tracks.ToListAsync());
    }

    [Theory]
    [InlineData("{\"type\":\"desktop.application.foreground\",\"version\":1,\"timeMode\":\"point\"}")]
    [InlineData("{\"type\":\"desktop.application.foreground\",\"version\":1,\"id\":\"019e0000-0000-7000-8000-000000000001\"}")]
    [InlineData("{\"type\":")]
    public async Task CallerCannotSetTrackFieldsOrSendMalformedJson(string body)
    {
        var ownerId = Guid.NewGuid();
        await ProvisionTimelineAsync(ownerId);
        await using var factory = RecordingApiFactory.Create(ConnectionString, new FixedTimeProvider(Now));
        using var client = factory.CreateClient();
        var collectorId = await RegisterAsync(client, ownerId);

        using var response = await ResolveAsync(client, ownerId, collectorId, body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        await using var db = CreateDbContext();
        Assert.Empty(await db.Tracks.ToListAsync());
    }

    [Fact]
    public async Task AnonymousCallerCannotResolveTrack()
    {
        await using var factory = RecordingApiFactory.Create(ConnectionString, new FixedTimeProvider(Now));
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync($"/api/v1/collectors/{Guid.NewGuid()}/tracks",
            new { type = "desktop.application.foreground", version = 1 });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ExistingTrackWithConflictingTimeModeIsNotRewritten()
    {
        var ownerId = Guid.NewGuid();
        await ProvisionTimelineAsync(ownerId);
        await using var factory = RecordingApiFactory.Create(ConnectionString, new FixedTimeProvider(Now));
        using var client = factory.CreateClient();
        var collectorId = await RegisterAsync(client, ownerId);
        var track = Track.Create(collectorId, RecordProtocols.ForegroundApplication.Type, 1,
            TimeMode.Point, null, Now.AddDays(-1));
        await using (var db = CreateDbContext())
        {
            db.Tracks.Add(track);
            await db.SaveChangesAsync();
        }

        using var response = await ResolveAsync(client, ownerId, collectorId);

        await AssertProblemAsync(response, HttpStatusCode.Conflict, "track_protocol_conflict");
        await using var verify = CreateDbContext();
        var stored = await verify.Tracks.SingleAsync();
        Assert.Equal(track.Id, stored.Id);
        Assert.Equal(TimeMode.Point, stored.TimeMode);
        Assert.Equal(Now.AddDays(-1), stored.CreatedAt);
    }

    private static async Task<Guid> RegisterAsync(
        HttpClient client, Guid ownerId, string key = "heartbeat.collector.desktop.macos")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/collectors")
        {
            Content = JsonContent.Create(new { key, target = "device-a", displayName = "My Mac" }),
        };
        request.Headers.Add(RecordingApiFactory.OwnerHeader, ownerId.ToString());
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<HttpResponseMessage> ResolveAsync(
        HttpClient client, Guid ownerId, Guid collectorId,
        string body = "{\"type\":\"desktop.application.foreground\",\"version\":1}")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/collectors/{collectorId}/tracks")
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(RecordingApiFactory.OwnerHeader, ownerId.ToString());
        return await client.SendAsync(request);
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, json.RootElement.GetProperty("code").GetString());
    }
}
