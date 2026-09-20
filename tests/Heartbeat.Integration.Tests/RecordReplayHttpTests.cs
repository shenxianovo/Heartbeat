using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Integration.Tests;

[Collection(PostgresTestGroup.Name)]
public sealed class RecordReplayHttpTests(PostgresFixture fixture) : PostgresTestBase(fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 14, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset BaseTime = new(2026, 9, 12, 13, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task OwnerCanReplayTrackRecordsInStableTimeOrder()
    {
        var ownerId = Guid.NewGuid();
        await ProvisionTimelineAsync(ownerId);
        await using var factory = RecordingApiFactory.Create(ConnectionString, new FixedTimeProvider(Now));
        using var client = factory.CreateClient();
        var trackId = await RegisterResolveAndUploadAsync(client, ownerId,
            Entry(Guid.CreateVersion7(), 10, 15, "com.editor"),
            Entry(Guid.CreateVersion7(), 12, 14, "com.browser"),
            Entry(Guid.CreateVersion7(), 17, 18, "com.mail"));

        using var response = await ReplayAsync(client, ownerId, trackId,
            from: BaseTime.AddMinutes(14),
            to: BaseTime.AddMinutes(18));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken: TestContext.Current.CancellationToken));
        var track = json.RootElement.GetProperty("track");
        Assert.Equal(trackId, track.GetProperty("id").GetGuid());
        Assert.Equal("desktop.application.foreground", track.GetProperty("type").GetString());
        Assert.Equal(1, track.GetProperty("version").GetInt32());
        Assert.Equal("range", track.GetProperty("timeMode").GetString());
        Assert.Equal("explicit", track.GetProperty("endMode").GetString());

        var records = json.RootElement.GetProperty("records");
        Assert.Equal(2, records.GetArrayLength());
        Assert.Equal(BaseTime.AddMinutes(10), records[0].GetProperty("startedAt").GetDateTimeOffset());
        Assert.Equal(BaseTime.AddMinutes(17), records[1].GetProperty("startedAt").GetDateTimeOffset());
        Assert.Equal("com.editor", ApplicationId(records[0]));
        Assert.Equal("com.mail", ApplicationId(records[1]));
        Assert.Equal(Now, records[0].GetProperty("receivedAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task LimitKeepsTheEarliestRecordsInReplayWindow()
    {
        var ownerId = Guid.NewGuid();
        await ProvisionTimelineAsync(ownerId);
        await using var factory = RecordingApiFactory.Create(ConnectionString, new FixedTimeProvider(Now));
        using var client = factory.CreateClient();
        var trackId = await RegisterResolveAndUploadAsync(client, ownerId,
            Entry(Guid.CreateVersion7(), 1, 2, "com.one"),
            Entry(Guid.CreateVersion7(), 2, 3, "com.two"),
            Entry(Guid.CreateVersion7(), 3, 4, "com.three"));

        using var response = await ReplayAsync(client, ownerId, trackId, limit: 2);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken: TestContext.Current.CancellationToken));
        var records = json.RootElement.GetProperty("records");
        Assert.Equal(2, records.GetArrayLength());
        Assert.Equal("com.one", ApplicationId(records[0]));
        Assert.Equal("com.two", ApplicationId(records[1]));
        Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("nextCursor").GetString()));
    }

    [Fact]
    public async Task CursorPagesAcrossEqualStartTimesWithoutDuplicatesOrOmissions()
    {
        var ownerId = Guid.NewGuid();
        await ProvisionTimelineAsync(ownerId);
        await using var factory = RecordingApiFactory.Create(ConnectionString, new FixedTimeProvider(Now));
        using var client = factory.CreateClient();
        var trackId = await RegisterResolveAndUploadAsync(client, ownerId,
            Entry(Guid.CreateVersion7(), 5, 6, "com.one"),
            Entry(Guid.CreateVersion7(), 5, 7, "com.two"),
            Entry(Guid.CreateVersion7(), 5, 8, "com.three"),
            Entry(Guid.CreateVersion7(), 5, 9, "com.four"),
            Entry(Guid.CreateVersion7(), 5, 10, "com.five"));

        using var completeResponse = await ReplayAsync(client, ownerId, trackId);
        completeResponse.EnsureSuccessStatusCode();
        using var complete = JsonDocument.Parse(await completeResponse.Content.ReadAsStringAsync(cancellationToken: TestContext.Current.CancellationToken));
        var expectedIds = complete.RootElement.GetProperty("records")
            .EnumerateArray()
            .Select(record => record.GetProperty("id").GetGuid())
            .ToArray();

        var pagedIds = new List<Guid>();
        string? cursor = null;
        do
        {
            using var pageResponse = await ReplayAsync(client, ownerId, trackId, limit: 2, cursor: cursor);
            pageResponse.EnsureSuccessStatusCode();
            using var page = JsonDocument.Parse(await pageResponse.Content.ReadAsStringAsync(cancellationToken: TestContext.Current.CancellationToken));
            pagedIds.AddRange(page.RootElement.GetProperty("records")
                .EnumerateArray()
                .Select(record => record.GetProperty("id").GetGuid()));
            cursor = page.RootElement.GetProperty("nextCursor").GetString();
        } while (cursor is not null);

        Assert.Equal(expectedIds, pagedIds);
        Assert.Equal(expectedIds.Length, pagedIds.Distinct().Count());
    }

    [Theory]
    [InlineData("not-base64!")]
    [InlineData("YmFk")]
    public async Task InvalidCursorIsRejected(string cursor)
    {
        var ownerId = Guid.NewGuid();
        await ProvisionTimelineAsync(ownerId);
        await using var factory = RecordingApiFactory.Create(ConnectionString, new FixedTimeProvider(Now));
        using var client = factory.CreateClient();
        var trackId = await RegisterResolveAndUploadAsync(client, ownerId,
            Entry(Guid.CreateVersion7(), 1, 2, "com.one"));

        using var response = await ReplayAsync(client, ownerId, trackId, cursor: cursor);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "invalid_request");
    }

    [Fact]
    public async Task ReplayPreservesArbitraryJsonValue()
    {
        var ownerId = Guid.NewGuid();
        await ProvisionTimelineAsync(ownerId);
        await using var factory = RecordingApiFactory.Create(ConnectionString, new FixedTimeProvider(Now));
        using var client = factory.CreateClient();
        var trackId = await RegisterAndResolveAsync(client, ownerId);
        using var expected = JsonDocument.Parse("[null,true,42.5,{\"nested\":[\"value\"]}]");
        using var upload = await UploadAsync(client, ownerId, trackId, new
        {
            records = new[]
            {
                new
                {
                    id = Guid.CreateVersion7(),
                    startedAt = BaseTime,
                    endedAt = BaseTime.AddMinutes(1),
                    value = expected.RootElement,
                },
            },
        });
        upload.EnsureSuccessStatusCode();

        using var response = await ReplayAsync(client, ownerId, trackId);
        response.EnsureSuccessStatusCode();
        using var replay = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken: TestContext.Current.CancellationToken));

        Assert.True(JsonElement.DeepEquals(
            expected.RootElement,
            replay.RootElement.GetProperty("records")[0].GetProperty("value")));
        Assert.Equal(JsonValueKind.Null, replay.RootElement.GetProperty("nextCursor").ValueKind);
    }

    [Fact]
    public async Task ForeignAndMissingTracksAreNotDisclosed()
    {
        var ownerId = Guid.NewGuid();
        var otherOwnerId = Guid.NewGuid();
        await ProvisionTimelineAsync(ownerId);
        await ProvisionTimelineAsync(otherOwnerId);
        await using var factory = RecordingApiFactory.Create(ConnectionString, new FixedTimeProvider(Now));
        using var client = factory.CreateClient();
        var trackId = await RegisterResolveAndUploadAsync(client, ownerId,
            Entry(Guid.CreateVersion7(), 1, 2, "com.one"),
            Entry(Guid.CreateVersion7(), 2, 3, "com.two"));

        using var firstPageResponse = await ReplayAsync(client, ownerId, trackId, limit: 1);
        firstPageResponse.EnsureSuccessStatusCode();
        using var firstPage = JsonDocument.Parse(await firstPageResponse.Content.ReadAsStringAsync(cancellationToken: TestContext.Current.CancellationToken));
        var cursor = firstPage.RootElement.GetProperty("nextCursor").GetString();

        using var foreign = await ReplayAsync(client, otherOwnerId, trackId, limit: 1, cursor: cursor);
        using var missing = await ReplayAsync(client, ownerId, Guid.NewGuid());

        await AssertProblemAsync(foreign, HttpStatusCode.NotFound, "track_not_found");
        await AssertProblemAsync(missing, HttpStatusCode.NotFound, "track_not_found");
    }

    [Theory]
    [InlineData("from_after_to")]
    [InlineData("zero_limit")]
    [InlineData("too_large_limit")]
    public async Task InvalidReplayQueryIsRejected(string invalid)
    {
        var ownerId = Guid.NewGuid();
        await ProvisionTimelineAsync(ownerId);
        await using var factory = RecordingApiFactory.Create(ConnectionString, new FixedTimeProvider(Now));
        using var client = factory.CreateClient();
        var trackId = await RegisterResolveAndUploadAsync(client, ownerId,
            Entry(Guid.CreateVersion7(), 1, 2, "com.one"));
        var path = invalid switch
        {
            "from_after_to" => ReplayPath(trackId, BaseTime.AddMinutes(2), BaseTime.AddMinutes(1), null),
            "zero_limit" => ReplayPath(trackId, null, null, 0),
            "too_large_limit" => ReplayPath(trackId, null, null, 501),
            _ => throw new InvalidOperationException("Unknown invalid query."),
        };

        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add(RecordingApiFactory.OwnerHeader, ownerId.ToString());
        using var response = await client.SendAsync(request, cancellationToken: TestContext.Current.CancellationToken);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "invalid_request");
    }

    [Fact]
    public async Task AnonymousCallerCannotReplay()
    {
        await using var factory = RecordingApiFactory.Create(ConnectionString, new FixedTimeProvider(Now));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/api/v1/tracks/{Guid.NewGuid()}/records", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private async Task<Guid> RegisterResolveAndUploadAsync(
        HttpClient client, Guid ownerId, params object[] records)
    {
        var trackId = await RegisterAndResolveAsync(client, ownerId);
        using var uploaded = await UploadAsync(client, ownerId, trackId, new { records });
        uploaded.EnsureSuccessStatusCode();
        await using var db = CreateDbContext();
        Assert.Equal(records.Length, await db.Records.CountAsync(record => record.TrackId == trackId));
        return trackId;
    }

    private static object Entry(Guid id, int startMinutes, int endMinutes, string application) => new
    {
        id,
        startedAt = BaseTime.AddMinutes(startMinutes),
        endedAt = BaseTime.AddMinutes(endMinutes),
        value = new
        {
            device_id = "device-a",
            application = new { platform = "macos", id_kind = "bundle_id", id = application },
        },
    };

    private static async Task<Guid> RegisterAndResolveAsync(HttpClient client, Guid ownerId)
    {
        using var registration = new HttpRequestMessage(HttpMethod.Post, "/api/v1/collectors")
        {
            Content = JsonContent.Create(new { key = "heartbeat.collector.desktop.macos", target = "device-a", displayName = "Mac" }),
        };
        registration.Headers.Add(RecordingApiFactory.OwnerHeader, ownerId.ToString());
        using var registered = await client.SendAsync(registration);
        registered.EnsureSuccessStatusCode();
        using var collector = JsonDocument.Parse(await registered.Content.ReadAsStringAsync());
        var collectorId = collector.RootElement.GetProperty("id").GetGuid();
        using var resolution = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/collectors/{collectorId}/tracks")
        {
            Content = JsonContent.Create(new
            {
                type = "desktop.application.foreground",
                version = 1,
                timeMode = "range",
                endMode = "explicit",
            }),
        };
        resolution.Headers.Add(RecordingApiFactory.OwnerHeader, ownerId.ToString());
        using var resolved = await client.SendAsync(resolution);
        resolved.EnsureSuccessStatusCode();
        using var track = JsonDocument.Parse(await resolved.Content.ReadAsStringAsync());
        return track.RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient client, Guid ownerId, Guid trackId, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/tracks/{trackId}/records")
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add(RecordingApiFactory.OwnerHeader, ownerId.ToString());
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> ReplayAsync(
        HttpClient client,
        Guid ownerId,
        Guid trackId,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        int? limit = null,
        string? cursor = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ReplayPath(trackId, from, to, limit, cursor));
        request.Headers.Add(RecordingApiFactory.OwnerHeader, ownerId.ToString());
        return await client.SendAsync(request);
    }

    private static string ReplayPath(
        Guid trackId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int? limit,
        string? cursor = null)
    {
        var parameters = new List<string>();
        if (from is not null)
        {
            parameters.Add($"from={Uri.EscapeDataString(from.Value.ToString("O"))}");
        }

        if (to is not null)
        {
            parameters.Add($"to={Uri.EscapeDataString(to.Value.ToString("O"))}");
        }

        if (limit is not null)
        {
            parameters.Add($"limit={limit.Value}");
        }

        if (cursor is not null)
        {
            parameters.Add($"cursor={Uri.EscapeDataString(cursor)}");
        }

        return parameters.Count == 0
            ? $"/api/v1/tracks/{trackId}/records"
            : $"/api/v1/tracks/{trackId}/records?{string.Join("&", parameters)}";
    }

    private static string? ApplicationId(JsonElement record) =>
        record.GetProperty("value").GetProperty("application").GetProperty("id").GetString();

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, json.RootElement.GetProperty("code").GetString());
    }
}
