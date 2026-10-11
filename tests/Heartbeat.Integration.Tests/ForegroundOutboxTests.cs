using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Heartbeat.Core;
using Heartbeat.Observers.ForegroundState;
using Microsoft.Data.Sqlite;

namespace Heartbeat.Integration.Tests;

public sealed class ForegroundOutboxTests
{
    [Test]
    [Property("caseId", "foreground-outbox-recovery")]
    public async Task ActivePeriodStaysLocalAndRestartClosesLastCheckpointExactlyOnce()
    {
        var observer = EntityId.New();
        using var local = new ForegroundTestStore(observer);
        var capture = Capture(observer).Through(ForegroundTimelineTests.Reading(1).ObservedAt);
        local.Store.Checkpoint(capture, null);
        await Assert.That(local.Store.Pending().Count).IsEqualTo(0);
        var restarted = new ForegroundStore(local.Path, observer);
        restarted.FinishActive();
        restarted.FinishActive();
        var pending = restarted.Pending().Single();
        await Assert.That(pending.ObservationId).IsEqualTo(capture.Observation.Id.Value);
        await Assert.That(JsonSerializer.Serialize(pending.Entities, JsonSerializerOptions.Web))
            .IsEqualTo(JsonSerializer.Serialize(capture.Submissions(), JsonSerializerOptions.Web));
        await Assert.That(() => new ForegroundStore(local.Path, EntityId.New())).Throws<InvalidDataException>();
        await Assert.That(restarted.Pending().Count).IsEqualTo(1);
    }

    [Test]
    [Property("caseId", "foreground-outbox-recovery")]
    public async Task FailedTransitionRollsBackBothOldPeriodAndNewCheckpoint()
    {
        var observer = EntityId.New();
        using var local = new ForegroundTestStore(observer);
        var first = Capture(observer);
        var second = Capture(observer);
        local.Store.Checkpoint(first, null);
        // Fail the second write, after the completed period was inserted in the same transaction.
        using (var connection = new SqliteConnection($"Data Source={local.Path};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER reject_checkpoint BEFORE UPDATE ON active_period BEGIN SELECT RAISE(ABORT, 'test failure'); END;";
            command.ExecuteNonQuery();
        }
        await Assert.That(() => local.Store.Checkpoint(second, first)).Throws<SqliteException>();
        await Assert.That(local.Store.Pending().Count).IsEqualTo(0);
        local.Store.FinishActive();
        await Assert.That(local.Store.Pending().Single().ObservationId).IsEqualTo(first.Observation.Id.Value);
    }

    [Test]
    [Property("caseId", "foreground-application-period-boundaries")]
    public async Task EventsBetweenPollsArePreservedAndSleepClosesWithoutExtending()
    {
        var observer = EntityId.New();
        using var local = new ForegroundTestStore(observer);
        var collector = new ForegroundCollector(observer, new LocalApplicationRegistry(local.Path + ".apps"),
            local.Store, TimeSpan.FromSeconds(1), () => ForegroundTimelineTests.Reading(2), TextWriter.Null, TextWriter.Null);
        collector.Observe(ForegroundTimelineTests.Reading(0));
        collector.Observe(ForegroundTimelineTests.Reading(0) with { Name = "B", ObservedAt = ForegroundTimelineTests.Reading(0).ObservedAt.AddMilliseconds(100), Elapsed = TimeSpan.FromMilliseconds(100), Awake = TimeSpan.FromMilliseconds(100) });
        collector.Observe(ForegroundTimelineTests.Reading(0) with { Name = "C", ObservedAt = ForegroundTimelineTests.Reading(0).ObservedAt.AddMilliseconds(200), Elapsed = TimeSpan.FromMilliseconds(200), Awake = TimeSpan.FromMilliseconds(200) });
        await Assert.That(local.Store.Pending().Count).IsEqualTo(2);
        collector.Sleep();
        collector.Poll();
        await Assert.That(local.Store.Pending().Count).IsEqualTo(3);
        collector.Wake();
        await Assert.That(local.Store.Pending().Count).IsEqualTo(3);
        collector.Stop();
        await Assert.That(local.Store.Pending().Count).IsEqualTo(4);
        var end = local.Store.Pending()[2].Entities[^1].Body.GetProperty("properties").GetProperty("endAt").GetDateTimeOffset();
        await Assert.That(end).IsEqualTo(ForegroundTimelineTests.Reading(0).ObservedAt.AddMilliseconds(200));
    }

    [Test]
    [Property("caseId", "foreground-outbox-recovery")]
    public async Task OfflineAndLostPutResponseKeepDataUntilLaterMatchingRead()
    {
        var observer = EntityId.New();
        using var local = new ForegroundTestStore(observer);
        local.Store.Checkpoint(Capture(observer), null);
        local.Store.FinishActive();
        using var handler = new UncertainDeliveryHandler();
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var sender = new OutboxSender(local.Store, client, TextWriter.Null);
        await sender.SynchronizeAsync(CancellationToken.None);
        await Assert.That(local.Store.Pending().Single().LastError).IsNotNull();
        // Local collection continues while the destination is unreachable.
        local.Store.Checkpoint(Capture(observer), null);
        await Assert.That(local.Store.Pending().Count).IsEqualTo(1);
        handler.Offline = false;
        await sender.SynchronizeAsync(CancellationToken.None);
        await Assert.That(local.Store.Pending().Count).IsEqualTo(1);
        await sender.SynchronizeAsync(CancellationToken.None);
        await Assert.That(local.Store.Pending().Count).IsEqualTo(1);
        await sender.SynchronizeAsync(CancellationToken.None);
        await Assert.That(local.Store.Pending().Count).IsEqualTo(0);
        await Assert.That(handler.PutCount).IsEqualTo(6);
    }

    private static ForegroundCapture Capture(EntityId observer) => ForegroundCapture.Create(observer,
        new LocalApplication(EntityId.New(), []), ForegroundTimelineTests.Reading(0));

    private sealed class UncertainDeliveryHandler : HttpMessageHandler
    {
        internal bool Offline { get; set; } = true;
        internal int PutCount { get; private set; }
        private readonly Dictionary<string, object> _saved = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Offline) throw new HttpRequestException("offline");
            var id = request.RequestUri!.Segments[^1];
            if (request.Method == HttpMethod.Get)
                return _saved.TryGetValue(id, out var value)
                    ? new(HttpStatusCode.OK) { Content = JsonContent.Create(value) }
                    : new(HttpStatusCode.NotFound);
            var body = await request.Content!.ReadFromJsonAsync<JsonElement>(cancellationToken);
            var path = request.RequestUri.AbsolutePath;
            var category = path.Contains("/observers/", StringComparison.Ordinal) ? "observer"
                : path.Contains("/schemas/", StringComparison.Ordinal) ? "entity_schema"
                : path.Contains("/observations/", StringComparison.Ordinal) ? "observation" : "entity";
            _saved[id] = new { category, references = body.GetProperty("references"), properties = body.GetProperty("properties") };
            PutCount++;
            if (PutCount == 1) throw new HttpRequestException("response lost after storing");
            return new(HttpStatusCode.Created);
        }
    }
}
