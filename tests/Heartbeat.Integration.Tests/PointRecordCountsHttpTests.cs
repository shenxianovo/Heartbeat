using System.Net;
using System.Text.Json;
using Heartbeat.Recording;

namespace Heartbeat.Integration.Tests;

[Collection(PostgresTestGroup.Name)]
public sealed class PointRecordCountsHttpTests(PostgresFixture fixture) : PostgresTestBase(fixture)
{
    private static readonly DateTimeOffset From = new(2026, 9, 12, 13, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CountsPointRecordsInQueryAlignedBuckets()
    {
        var ownerId = Guid.NewGuid();
        var track = await SeedTrackAsync(ownerId, TimeMode.Point,
            From,
            From.AddSeconds(59),
            From.AddSeconds(60),
            From.AddSeconds(179),
            From.AddSeconds(180));
        await using var factory = RecordingApiFactory.Create(ConnectionString, new FixedTimeProvider(From.AddHours(1)));
        using var client = factory.CreateClient();

        using var response = await GetAsync(client, ownerId, track.Id, From, From.AddSeconds(180), 60);

        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(60, json.RootElement.GetProperty("bucketSeconds").GetInt32());
        var buckets = json.RootElement.GetProperty("buckets");
        Assert.Equal(3, buckets.GetArrayLength());
        Assert.Equal((0, 2L), ReadBucket(buckets[0]));
        Assert.Equal((1, 1L), ReadBucket(buckets[1]));
        Assert.Equal((2, 1L), ReadBucket(buckets[2]));
        Assert.Equal(From.AddMinutes(1), buckets[0].GetProperty("endedAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task RangeTrackCannotUsePointCounts()
    {
        var ownerId = Guid.NewGuid();
        var track = await SeedTrackAsync(ownerId, TimeMode.Range);
        await using var factory = RecordingApiFactory.Create(ConnectionString, new FixedTimeProvider(From.AddHours(1)));
        using var client = factory.CreateClient();

        using var response = await GetAsync(client, ownerId, track.Id, From, From.AddMinutes(1), 10);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "track_is_not_point");
    }

    [Fact]
    public async Task ForeignTrackAndExcessiveBucketCountAreRejected()
    {
        var ownerId = Guid.NewGuid();
        var otherOwnerId = Guid.NewGuid();
        var track = await SeedTrackAsync(ownerId, TimeMode.Point, From);
        await ProvisionTimelineAsync(otherOwnerId);
        await using var factory = RecordingApiFactory.Create(ConnectionString, new FixedTimeProvider(From.AddHours(1)));
        using var client = factory.CreateClient();

        using var foreign = await GetAsync(client, otherOwnerId, track.Id, From, From.AddMinutes(1), 10);
        using var excessive = await GetAsync(client, ownerId, track.Id, From, From.AddSeconds(10_001), 1);

        await AssertProblemAsync(foreign, HttpStatusCode.NotFound, "track_not_found");
        await AssertProblemAsync(excessive, HttpStatusCode.BadRequest, "invalid_request");
    }

    private async Task<Track> SeedTrackAsync(Guid ownerId, TimeMode timeMode, params DateTimeOffset[] points)
    {
        await ProvisionTimelineAsync(ownerId);
        await using var db = CreateDbContext();
        var timeline = db.Timelines.Single(item => item.OwnerId == ownerId);
        var collector = Heartbeat.Recording.Collector.Create(timeline.Id, "heartbeat.collector.desktop.macos",
            $"device-{Guid.NewGuid():N}", "Mac", From);
        var track = Track.Create(collector.Id,
            timeMode == TimeMode.Point ? "desktop.input.event" : "desktop.system.away",
            1, timeMode, timeMode == TimeMode.Point ? null : EndMode.Explicit, From);
        db.Collectors.Add(collector);
        db.Tracks.Add(track);
        using var value = JsonDocument.Parse("""{"device_id":"device-a"}""");
        foreach (var point in points)
        {
            db.Records.Add(Heartbeat.Recording.Record.Create(Guid.CreateVersion7(point), track, point,
                timeMode == TimeMode.Range ? point.AddSeconds(1) : null, null,
                From.AddHours(1), value.RootElement));
        }
        await db.SaveChangesAsync();
        return track;
    }

    private static async Task<HttpResponseMessage> GetAsync(
        HttpClient client, Guid ownerId, Guid trackId,
        DateTimeOffset from, DateTimeOffset to, int bucketSeconds)
    {
        var path = $"/api/v1/tracks/{trackId}/point-counts" +
            $"?from={Uri.EscapeDataString(from.ToString("O"))}" +
            $"&to={Uri.EscapeDataString(to.ToString("O"))}" +
            $"&bucketSeconds={bucketSeconds}";
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add(RecordingApiFactory.OwnerHeader, ownerId.ToString());
        return await client.SendAsync(request);
    }

    private static (int Index, long Count) ReadBucket(JsonElement bucket) =>
        (bucket.GetProperty("index").GetInt32(), bucket.GetProperty("count").GetInt64());

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, json.RootElement.GetProperty("code").GetString());
    }
}
