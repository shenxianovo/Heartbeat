using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Heartbeat.Hub;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Integration.Tests;

[Collection(PostgresTestGroup.Name)]
public sealed class HubDeliveryTests(PostgresFixture fixture) : PostgresTestBase(fixture)
{
    [Fact]
    public async Task OfflineFirstSubmissionResolvesAfterRestartAndSurvivesLostBackendReceipt()
    {
        var owner = Guid.NewGuid();
        await ProvisionTimelineAsync(owner);
        var now = DateTimeOffset.UtcNow.AddTicks(7);
        await using var factory = RecordingApiFactory.Create(ConnectionString, new FixedTimeProvider(now));
        using var replayClient = factory.CreateClient();
        replayClient.DefaultRequestHeaders.Add(RecordingApiFactory.OwnerHeader, owner.ToString());
        var directory = Path.Combine(Path.GetTempPath(), $"heartbeat-hub-postgres-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "queue.sqlite");
        var destination = new DeliveryDestination(replayClient.BaseAddress!, owner);
        try
        {
            var record = new RecordSnapshot(Guid.CreateVersion7(), now.AddMinutes(-2), now.AddMinutes(-1), null,
                JsonSerializer.SerializeToElement(new { customObservation = "any collector value" }));
            var submission = new HubSubmission(
                new CollectorDeclaration("example.collector", "test-target", "Test collector"),
                new TrackDeclaration("example.unregistered.range", 1, "range", "explicit"), [record]);
            var queue = new RecordOutbox(path, destination);
            queue.Accept(submission);
            using (var offline = new HttpClient(new OfflineHandler()))
            {
                Assert.Single(await new RecordUploader(queue, offline, Token(owner)).UploadOnceAsync());
            }

            await using (var db = CreateDbContext())
            {
                Assert.Empty(await db.Collectors.ToListAsync());
                Assert.Empty(await db.Tracks.ToListAsync());
            }

            queue = new RecordOutbox(path, destination);
            Assert.Null(Assert.Single(queue.TakePending()).Route.BackendTrackId);
            using var handler = new BackendHandler(owner, loseFirstRecordReply: true)
            {
                InnerHandler = factory.Server.CreateHandler(),
            };
            using var backend = new HttpClient(handler);
            Assert.Single(await new RecordUploader(queue, backend, Token(owner)).UploadOnceAsync());
            Assert.Equal(new QueueStatus(1, 0), queue.Status());
            var mappedTrack = Assert.Single(queue.TakePending()).Route.BackendTrackId!.Value;
            await using (var db = CreateDbContext())
            {
                Assert.Equal(record.Id, (await db.Records.SingleAsync()).Id);
            }

            queue = new RecordOutbox(path, destination);
            queue.Accept(submission with { Records = [record with { EndedAt = now }] });
            Assert.Empty(await new RecordUploader(queue, backend, Token(owner)).UploadOnceAsync());
            Assert.Equal(new QueueStatus(0, 0), queue.Status());
            Assert.Equal(1, handler.RegistrationRequests);
            Assert.Equal(1, handler.TrackRequests);

            using var replay = await replayClient.GetAsync($"/api/v1/tracks/{mappedTrack}/records");
            replay.EnsureSuccessStatusCode();
            var body = await replay.Content.ReadFromJsonAsync<JsonElement>();
            var stored = Assert.Single(body.GetProperty("records").EnumerateArray());
            Assert.Equal(record.Id, stored.GetProperty("id").GetGuid());
            Assert.True(JsonElement.DeepEquals(record.Value, stored.GetProperty("value")));
            Assert.True(Math.Abs((stored.GetProperty("endedAt").GetDateTimeOffset() - now).Ticks) < 10);

            // Reusing an ID with a different value still conflicts after the local queue has drained.
            queue.Accept(submission with
            {
                Records = [record with { Value = JsonSerializer.SerializeToElement("conflicting value") }],
            });
            Assert.Empty(await new RecordUploader(queue, backend, Token(owner)).UploadOnceAsync());
            Assert.Equal(new QueueStatus(0, 1), queue.Status());
            Assert.Equal("conflict", Assert.Single(queue.ReadFailures()).Failure);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("point", null)]
    [InlineData("range", "next_record")]
    public async Task UnknownDataTypeWithNoExplicitEndTravelsThroughHubAndReplays(
        string timeMode, string? endMode)
    {
        var owner = Guid.NewGuid();
        await ProvisionTimelineAsync(owner);
        var now = DateTimeOffset.UtcNow;
        await using var factory = RecordingApiFactory.Create(ConnectionString, new FixedTimeProvider(now));
        using var replayClient = factory.CreateClient();
        replayClient.DefaultRequestHeaders.Add(RecordingApiFactory.OwnerHeader, owner.ToString());
        var directory = Path.Combine(Path.GetTempPath(), $"heartbeat-hub-shape-{Guid.NewGuid():N}");
        try
        {
            var record = new RecordSnapshot(Guid.CreateVersion7(), now, null, null,
                JsonSerializer.SerializeToElement(new object?[] { "custom", 42, null, new { nested = true } }));
            var queue = new RecordOutbox(Path.Combine(directory, "queue.sqlite"),
                new DeliveryDestination(replayClient.BaseAddress!, owner));
            queue.Accept(new HubSubmission(
                new CollectorDeclaration("example.custom", "test-target", "Custom collector"),
                new TrackDeclaration("example.unregistered.data", 1, timeMode, endMode), [record]));
            using var handler = new BackendHandler(owner) { InnerHandler = factory.Server.CreateHandler() };
            using var backend = new HttpClient(handler);
            Assert.Empty(await new RecordUploader(queue, backend, Token(owner)).UploadOnceAsync());
            Assert.Equal(new QueueStatus(0, 0), queue.Status());

            await using var db = CreateDbContext();
            var trackId = (await db.Tracks.SingleAsync()).Id;
            using var replay = await replayClient.GetAsync($"/api/v1/tracks/{trackId}/records");
            replay.EnsureSuccessStatusCode();
            var body = await replay.Content.ReadFromJsonAsync<JsonElement>();
            var stored = Assert.Single(body.GetProperty("records").EnumerateArray());
            Assert.Equal(record.Id, stored.GetProperty("id").GetGuid());
            Assert.Equal(JsonValueKind.Null, stored.GetProperty("endedAt").ValueKind);
            Assert.True(JsonElement.DeepEquals(record.Value, stored.GetProperty("value")));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static string Token(Guid owner) =>
        $"e30.{Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { sub = owner }))).TrimEnd('=').Replace('+', '-').Replace('/', '_')}.test";

    private sealed class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("Backend offline"));
    }

    private sealed class BackendHandler(Guid owner, bool loseFirstRecordReply = false) : DelegatingHandler
    {
        private bool _loseReply = loseFirstRecordReply;
        public int RegistrationRequests { get; private set; }
        public int TrackRequests { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Add(RecordingApiFactory.OwnerHeader, owner.ToString());
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/v1/collectors") RegistrationRequests++;
            if (path.EndsWith("/tracks", StringComparison.Ordinal)) TrackRequests++;
            var response = await base.SendAsync(request, cancellationToken);
            if (_loseReply && path.EndsWith("/records", StringComparison.Ordinal))
            {
                _loseReply = false;
                response.Dispose();
                throw new HttpRequestException("Lost response after real PostgreSQL writes.");
            }

            return response;
        }
    }
}
