using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Heartbeat.Application.Recording;
using Heartbeat.Recording;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RecordingRecord = Heartbeat.Recording.Record;

namespace Heartbeat.Integration.Tests;

[Collection(PostgresTestGroup.Name)]
public sealed class RecordUploadHttpTests(PostgresFixture fixture) : PostgresTestBase(fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset StartedAt = Now.AddMinutes(-10);
    private static readonly string[] MixedBatchStatuses = ["stored", "conflict", "invalid_record", "invalid_record", "stored"];

    [Fact]
    public async Task UnknownPointTypeCanBeCreatedUploadedAndReplayedWithArbitraryJson()
    {
        var ownerId = Guid.NewGuid();
        await ProvisionTimelineAsync(ownerId);
        await using var factory = RecordingApiFactory.Create(ConnectionString, new FixedTimeProvider(Now));
        using var client = factory.CreateClient();
        var collectorId = await RegisterAsync(client, ownerId);
        using var resolution = await ResolveAsync(client, ownerId, collectorId,
            new { type = "custom.sensor.sample", version = 37, timeMode = "point", endMode = (string?)null });
        resolution.EnsureSuccessStatusCode();
        using var resolved = JsonDocument.Parse(await resolution.Content.ReadAsStringAsync());
        var trackId = resolved.RootElement.GetProperty("id").GetGuid();
        var recordId = Guid.CreateVersion7();
        var value = new object?[] { "sample", 42, true, null, new { nested = new[] { 1, 2, 3 } } };

        using var uploaded = await UploadAsync(client, ownerId, trackId, new
        {
            records = new[] { new { id = recordId, startedAt = StartedAt, value } },
        });
        uploaded.EnsureSuccessStatusCode();
        using var replayed = await GetRecordsAsync(client, ownerId, trackId);
        replayed.EnsureSuccessStatusCode();

        using var replay = JsonDocument.Parse(await replayed.Content.ReadAsStringAsync());
        var track = replay.RootElement.GetProperty("track");
        Assert.Equal("custom.sensor.sample", track.GetProperty("type").GetString());
        Assert.Equal(37, track.GetProperty("version").GetInt32());
        Assert.Equal("point", track.GetProperty("timeMode").GetString());
        Assert.Equal(JsonValueKind.Null, track.GetProperty("endMode").ValueKind);
        var record = Assert.Single(replay.RootElement.GetProperty("records").EnumerateArray());
        Assert.Equal(recordId, record.GetProperty("id").GetGuid());
        Assert.Equal(JsonValueKind.Null, record.GetProperty("endedAt").ValueKind);
        Assert.Equal(42, record.GetProperty("value")[1].GetInt32());
        Assert.Equal(3, record.GetProperty("value")[4].GetProperty("nested")[2].GetInt32());
    }

    [Fact]
    public async Task RegisteredCollectorCanResolveTrackAndUploadBatch()
    {
        var ownerId = Guid.NewGuid();
        await ProvisionTimelineAsync(ownerId);
        await using var factory = RecordingApiFactory.Create(ConnectionString, new FixedTimeProvider(Now));
        using var client = factory.CreateClient();
        var trackId = await RegisterAndResolveAsync(client, ownerId);
        var firstId = Guid.CreateVersion7();
        var secondId = Guid.CreateVersion7();

        using var response = await UploadAsync(client, ownerId, trackId,
            new { records = new[] { Entry(firstId, 1), Entry(secondId, 2, "com.apple.finder") } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var results = json.RootElement.GetProperty("results");
        Assert.Equal(2, results.GetArrayLength());
        AssertReceipt(results[0], 0, firstId, StartedAt.AddMinutes(1));
        AssertReceipt(results[1], 1, secondId, StartedAt.AddMinutes(2));
        await using var db = CreateDbContext();
        var records = await db.Records.OrderBy(record => record.EndedAt).ToListAsync();
        Assert.Equal(2, records.Count);
        Assert.All(records, record => Assert.Equal(trackId, record.TrackId));
        Assert.All(records, record => Assert.Equal(Now, record.ReceivedAt));
        Assert.All(records, record => Assert.Null(record.ObservedAt));
        Assert.Equal("com.apple.finder", records[1].Value.GetProperty("application").GetProperty("id").GetString());
    }

    [Fact]
    public async Task RepeatedIdsAndOutOfOrderBatchesConfirmLargestEnd()
    {
        var ownerId = Guid.NewGuid();
        await ProvisionTimelineAsync(ownerId);
        await using var factory = RecordingApiFactory.Create(ConnectionString, new FixedTimeProvider(Now));
        using var client = factory.CreateClient();
        var trackId = await RegisterAndResolveAsync(client, ownerId);
        var id = Guid.CreateVersion7();
        var body = new { records = new[] { Entry(id, 3), Entry(id, 5), Entry(id, 1) } };

        using var first = await UploadAsync(client, ownerId, trackId, body);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var firstJson = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        var firstResults = firstJson.RootElement.GetProperty("results");
        AssertReceipt(firstResults[0], 0, id, StartedAt.AddMinutes(3));
        AssertReceipt(firstResults[1], 1, id, StartedAt.AddMinutes(5));
        AssertReceipt(firstResults[2], 2, id, StartedAt.AddMinutes(5));

        await using var retryFactory = RecordingApiFactory.Create(ConnectionString, new FixedTimeProvider(Now.AddHours(1)));
        using var retryClient = retryFactory.CreateClient();
        using var retry = await UploadAsync(retryClient, ownerId, trackId, body);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        using var retryJson = JsonDocument.Parse(await retry.Content.ReadAsStringAsync());
        foreach (var result in retryJson.RootElement.GetProperty("results").EnumerateArray())
        {
            Assert.Equal("stored", result.GetProperty("status").GetString());
            Assert.Equal(StartedAt.AddMinutes(5), result.GetProperty("endedAt").GetDateTimeOffset());
            Assert.Equal(Now, result.GetProperty("receivedAt").GetDateTimeOffset());
        }

        await using var db = CreateDbContext();
        Assert.Equal(StartedAt.AddMinutes(5), (await db.Records.SingleAsync()).EndedAt);
    }

    [Fact]
    public async Task ConflictAndInvalidRecordDoNotBlockOtherEntries()
    {
        var ownerId = Guid.NewGuid();
        await ProvisionTimelineAsync(ownerId);
        await using var factory = RecordingApiFactory.Create(ConnectionString, new FixedTimeProvider(Now));
        using var client = factory.CreateClient();
        var trackId = await RegisterAndResolveAsync(client, ownerId);
        var firstId = Guid.CreateVersion7();
        var secondId = Guid.CreateVersion7();

        using var response = await UploadAsync(client, ownerId, trackId, new
        {
            records = new object?[]
            {
                Entry(firstId, 1),
                Entry(firstId, 5, "different.application"),
                new { id = Guid.CreateVersion7(), endedAt = Now, value = Value() },
                null,
                Entry(secondId, 2),
            },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var results = json.RootElement.GetProperty("results");
        Assert.Equal(MixedBatchStatuses,
            results.EnumerateArray().Select(result => result.GetProperty("status").GetString()));
        Assert.Equal(Enumerable.Range(0, 5), results.EnumerateArray().Select(result => result.GetProperty("index").GetInt32()));
        Assert.Equal(JsonValueKind.Null, results[3].GetProperty("id").ValueKind);
        await using var db = CreateDbContext();
        var records = await db.Records.ToDictionaryAsync(record => record.Id);
        Assert.Equal(2, records.Count);
        Assert.Equal(StartedAt.AddMinutes(1), records[firstId].EndedAt);
        Assert.Equal(StartedAt.AddMinutes(2), records[secondId].EndedAt);
    }

    [Fact]
    public async Task BatchCanBeRetriedAfterFailureFollowingOneCommittedRecord()
    {
        var ownerId = Guid.NewGuid();
        await ProvisionTimelineAsync(ownerId);
        await using var factory = RecordingApiFactory.Create(ConnectionString, new FixedTimeProvider(Now));
        using var client = factory.CreateClient();
        var trackId = await RegisterAndResolveAsync(client, ownerId);
        var body = new { records = new[] { Entry(Guid.CreateVersion7(), 1), Entry(Guid.CreateVersion7(), 2) } };
        await using var failureFactory = factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                var implementation = services.Single(service => service.ServiceType == typeof(IRecordStore)).ImplementationType!;
                services.Replace(ServiceDescriptor.Scoped<IRecordStore>(provider =>
                    new FailSecondWriteStore((IRecordStore)ActivatorUtilities.CreateInstance(provider, implementation))));
            }));
        using var failureClient = failureFactory.CreateClient();

        using var failed = await UploadAsync(failureClient, ownerId, trackId, body);

        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        await using (var db = CreateDbContext())
        {
            Assert.Single(await db.Records.ToListAsync());
        }

        using var retry = await UploadAsync(client, ownerId, trackId, body);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        using var json = JsonDocument.Parse(await retry.Content.ReadAsStringAsync());
        Assert.All(json.RootElement.GetProperty("results").EnumerateArray(), result =>
            Assert.Equal("stored", result.GetProperty("status").GetString()));
        await using var verify = CreateDbContext();
        Assert.Equal(2, await verify.Records.CountAsync());
    }

    [Theory]
    [InlineData("id")]
    [InlineData("end")]
    [InlineData("backwards")]
    public async Task InvalidObservationIsRejectedBeforeStorage(string invalidField)
    {
        var ownerId = Guid.NewGuid();
        await ProvisionTimelineAsync(ownerId);
        await using var factory = RecordingApiFactory.Create(ConnectionString, new FixedTimeProvider(Now));
        using var client = factory.CreateClient();
        var trackId = await RegisterAndResolveAsync(client, ownerId);
        var entry = new Dictionary<string, object?>
        {
            ["id"] = invalidField == "id" ? Guid.NewGuid() : Guid.CreateVersion7(),
            ["startedAt"] = StartedAt,
            ["endedAt"] = invalidField == "end" ? null : invalidField == "backwards" ? StartedAt.AddSeconds(-1) : Now,
            ["value"] = Value(),
        };

        using var response = await UploadAsync(client, ownerId, trackId, new { records = new[] { entry } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("invalid_record", json.RootElement.GetProperty("results")[0].GetProperty("status").GetString());
        await using var db = CreateDbContext();
        Assert.Empty(await db.Records.ToListAsync());
    }

    [Fact]
    public async Task ForeignAndMissingTracksAreNotDisclosedOrWritten()
    {
        var ownerId = Guid.NewGuid();
        await ProvisionTimelineAsync(ownerId);
        await using var factory = RecordingApiFactory.Create(ConnectionString, new FixedTimeProvider(Now));
        using var client = factory.CreateClient();
        var trackId = await RegisterAndResolveAsync(client, ownerId);
        var body = new { records = new[] { Entry(Guid.CreateVersion7(), 1) } };

        using var foreign = await UploadAsync(client, Guid.NewGuid(), trackId, body);
        using var missing = await UploadAsync(client, ownerId, Guid.NewGuid(), body);

        await AssertProblemAsync(foreign, HttpStatusCode.NotFound, "track_not_found");
        await AssertProblemAsync(missing, HttpStatusCode.NotFound, "track_not_found");
        await using var db = CreateDbContext();
        Assert.Empty(await db.Records.ToListAsync());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"records\":null}")]
    [InlineData("{\"records\":[]}")]
    public async Task MissingOrEmptyBatchIsRejected(string body)
    {
        await using var factory = RecordingApiFactory.Create(ConnectionString, new FixedTimeProvider(Now));
        using var client = factory.CreateClient();

        using var response = await UploadRawAsync(client, Guid.NewGuid(), Guid.NewGuid(), body);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "invalid_request");
    }

    [Fact]
    public async Task BatchLimitIsCheckedBeforeAnyWrites()
    {
        var ownerId = Guid.NewGuid();
        await ProvisionTimelineAsync(ownerId);
        await using var factory = RecordingApiFactory.Create(ConnectionString, new FixedTimeProvider(Now));
        using var client = factory.CreateClient();
        var trackId = await RegisterAndResolveAsync(client, ownerId);
        var entries = Enumerable.Range(0, 501).Select(_ => Entry(Guid.CreateVersion7(), 1)).ToArray();

        using var response = await UploadAsync(client, ownerId, trackId, new { records = entries });

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "invalid_request");
        await using var db = CreateDbContext();
        Assert.Empty(await db.Records.ToListAsync());
    }

    [Fact]
    public async Task CallerCannotProvideReceiptTimeOrOtherServerFields()
    {
        var ownerId = Guid.NewGuid();
        await ProvisionTimelineAsync(ownerId);
        await using var factory = RecordingApiFactory.Create(ConnectionString, new FixedTimeProvider(Now));
        using var client = factory.CreateClient();
        var trackId = await RegisterAndResolveAsync(client, ownerId);

        using var response = await UploadAsync(client, ownerId, trackId, new
        {
            records = new object[]
            {
                Entry(Guid.CreateVersion7(), 1),
                new { id = Guid.CreateVersion7(), startedAt = StartedAt, endedAt = Now, value = Value(), receivedAt = Now.AddDays(-1) },
            },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using var db = CreateDbContext();
        Assert.Empty(await db.Records.ToListAsync());
    }

    [Fact]
    public async Task AnonymousCallerCannotUpload()
    {
        await using var factory = RecordingApiFactory.Create(ConnectionString, new FixedTimeProvider(Now));
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync($"/api/v1/tracks/{Guid.NewGuid()}/records",
            new { records = new[] { Entry(Guid.CreateVersion7(), 1) } });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static object Entry(Guid id, int minutes, string application = "com.google.Chrome") => new
    {
        id,
        startedAt = StartedAt,
        endedAt = StartedAt.AddMinutes(minutes),
        observedAt = StartedAt,
        value = Value(application),
    };

    private static object Value(string application = "com.google.Chrome") => new
    {
        device_id = "device-a",
        application = new { platform = "macos", id_kind = "bundle_id", id = application },
    };

    private static async Task<Guid> RegisterAndResolveAsync(HttpClient client, Guid ownerId)
    {
        var collectorId = await RegisterAsync(client, ownerId);
        using var resolved = await ResolveAsync(client, ownerId, collectorId,
            new { type = "desktop.application.foreground", version = 1, timeMode = "range", endMode = "explicit" });
        resolved.EnsureSuccessStatusCode();
        using var track = JsonDocument.Parse(await resolved.Content.ReadAsStringAsync());
        return track.RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<Guid> RegisterAsync(HttpClient client, Guid ownerId)
    {
        using var registration = new HttpRequestMessage(HttpMethod.Post, "/api/v1/collectors")
        {
            Content = JsonContent.Create(new { key = "heartbeat.collector.desktop.macos", target = "device-a", displayName = "Mac" }),
        };
        registration.Headers.Add(RecordingApiFactory.OwnerHeader, ownerId.ToString());
        using var registered = await client.SendAsync(registration);
        registered.EnsureSuccessStatusCode();
        using var collector = JsonDocument.Parse(await registered.Content.ReadAsStringAsync());
        return collector.RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<HttpResponseMessage> ResolveAsync(
        HttpClient client, Guid ownerId, Guid collectorId, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/collectors/{collectorId}/tracks")
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add(RecordingApiFactory.OwnerHeader, ownerId.ToString());
        return await client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> UploadAsync(HttpClient client, Guid ownerId, Guid trackId, object body) =>
        UploadRawAsync(client, ownerId, trackId, JsonSerializer.Serialize(body));

    private static async Task<HttpResponseMessage> UploadRawAsync(HttpClient client, Guid ownerId, Guid trackId, string body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/tracks/{trackId}/records")
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(RecordingApiFactory.OwnerHeader, ownerId.ToString());
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> GetRecordsAsync(HttpClient client, Guid ownerId, Guid trackId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/tracks/{trackId}/records");
        request.Headers.Add(RecordingApiFactory.OwnerHeader, ownerId.ToString());
        return await client.SendAsync(request);
    }

    private static void AssertReceipt(JsonElement result, int index, Guid id, DateTimeOffset endedAt)
    {
        Assert.Equal(index, result.GetProperty("index").GetInt32());
        Assert.Equal(id, result.GetProperty("id").GetGuid());
        Assert.Equal("stored", result.GetProperty("status").GetString());
        Assert.Equal(endedAt, result.GetProperty("endedAt").GetDateTimeOffset());
        Assert.Equal(Now, result.GetProperty("receivedAt").GetDateTimeOffset());
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, json.RootElement.GetProperty("code").GetString());
    }

    private sealed class FailSecondWriteStore(IRecordStore inner) : IRecordStore
    {
        private int _writes;

        public Task<RecordWriteResult> WriteAsync(
            Guid ownerId, RecordingRecord record, CancellationToken cancellationToken = default)
        {
            if (++_writes == 2)
            {
                throw new InvalidOperationException("Simulated infrastructure failure.");
            }

            return inner.WriteAsync(ownerId, record, cancellationToken);
        }
    }
}
