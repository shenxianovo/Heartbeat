using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Heartbeat.Core;
using Heartbeat.Observers.ForegroundState;
using Heartbeat.Testing;

namespace Heartbeat.Integration.Tests;

public sealed class MacObserverSubmissionTests
{
    private TestDatabase? _database;

    [Before(Test)]
    public async Task CreateDatabaseAsync(CancellationToken cancellationToken) =>
        _database = await PostgresAssemblyHooks.Instance.CreateDatabaseAsync(cancellationToken);

    [After(Test)]
    public async Task RemoveDatabaseAsync()
    {
        if (_database is not null) await _database.DisposeAsync();
    }

    [Test]
    public async Task CapturedReadingsRetainIndependentContentAndFactTimes()
    {
        var cancellationToken = TestContext.Current!.Execution.CancellationToken;
        await using var factory = new HeartbeatApiFactory(_database!.ConnectionString);
        using var client = factory.CreateClient();
        var observerId = EntityId.New();
        var time = DateTimeOffset.Parse("2026-10-07T09:00:00.1234567+08:00", CultureInfo.InvariantCulture);
        var application = new LocalApplication(EntityId.New(), [new ApplicationIdentifier("macos.bundle-id", "com.example.app")]);
        var first = ForegroundCapture.Create(observerId, application,
            new ForegroundApplicationReading("com.example.app", "应用 A", null, time, "Asia/Shanghai"));
        var second = ForegroundCapture.Create(observerId, application,
            new ForegroundApplicationReading("com.example.app", "应用 B", "/Applications/B.app", time.AddSeconds(1),
                "Asia/Shanghai"));
        using var local = new ForegroundTestStore(observerId);
        foreach (var capture in new[] { first, second })
        {
            local.Store.Checkpoint(capture, null);
            local.Store.FinishActive();
        }
        var sender = new OutboxSender(local.Store, client, TextWriter.Null);
        await sender.SynchronizeAsync(cancellationToken);
        await sender.SynchronizeAsync(cancellationToken);
        await Assert.That(local.Store.Pending().Count).IsEqualTo(0);
        await Assert.That(first.Content.Id == second.Content.Id).IsFalse();
        await Assert.That(first.Observation.Id == second.Observation.Id).IsFalse();
        await Assert.That(first.Observation.ObserverId).IsEqualTo(second.Observation.ObserverId);
        await Assert.That(first.Observation.SchemaId).IsEqualTo(second.Observation.SchemaId);

        var oldContent = await client.GetFromJsonAsync<JsonElement>($"entities/{first.Content.Id.Value}", cancellationToken);
        await Assert.That(oldContent.GetProperty("properties").GetProperty("name").GetString()).IsEqualTo("应用 A");
        await Assert.That(oldContent.GetProperty("references").GetProperty("localApplicationId").GetGuid())
            .IsEqualTo(application.Id.Value);
        await Assert.That(first.Content.GetReferences().Single()).IsEqualTo(application.Id);
        var observation = await client.GetFromJsonAsync<JsonElement>(
            $"entities/{first.Observation.Id.Value}", cancellationToken);
        var body = observation.GetProperty("properties");
        await Assert.That(body.GetProperty("startAt").GetString()).IsEqualTo("2026-10-07T01:00:00.123456Z");
        await Assert.That(body.GetProperty("endAt").GetString()).IsEqualTo(body.GetProperty("startAt").GetString());
    }

    [Test]
    public async Task SubmissionRegistersItsSchemaAndVerifiesEveryEntity()
    {
        var cancellationToken = TestContext.Current!.Execution.CancellationToken;
        await using var factory = new HeartbeatApiFactory(_database!.ConnectionString);
        using var client = factory.CreateClient();
        var capture = ForegroundCapture.Create(EntityId.New(), new LocalApplication(EntityId.New(), []),
            new ForegroundApplicationReading(null, null, null, DateTimeOffset.UtcNow, null));
        using var local = new ForegroundTestStore(capture.Observer.Id);
        local.Store.Checkpoint(capture, null);
        local.Store.FinishActive();
        var sender = new OutboxSender(local.Store, client, TextWriter.Null);
        await sender.SynchronizeAsync(cancellationToken);
        await Assert.That(local.Store.Pending().Count).IsEqualTo(1);
        await sender.SynchronizeAsync(cancellationToken);
        await Assert.That(local.Store.Pending().Count).IsEqualTo(0);
        foreach (var entity in capture.Submissions())
            await Assert.That(entity.Matches(await client.GetFromJsonAsync<JsonElement>($"entities/{entity.Id}", cancellationToken))).IsTrue();
    }

    [Test]
    [Property("caseId", "foreground-period-submission")]
    public async Task CollectorSavesIntervalsAndResumesAfterReadFailureWithoutChangingHistory()
    {
        var cancellationToken = TestContext.Current!.Execution.CancellationToken;
        await using var factory = new HeartbeatApiFactory(_database!.ConnectionString);
        using var client = factory.CreateClient();
        var directory = Directory.CreateTempSubdirectory("heartbeat-period-submission-");
        try
        {
            var attempt = 0;
            ForegroundApplicationReading Read()
            {
                var second = attempt++;
                if (second == 3) throw new InvalidOperationException("Unavailable foreground app");
                var reading = ForegroundTimelineTests.Reading(second);
                return second >= 2 ? reading with { Name = "Renamed" } : reading;
            }
            using var output = new StringWriter(CultureInfo.InvariantCulture);
            var observerId = EntityId.New();
            using var local = new ForegroundTestStore(observerId);
            var collector = new ForegroundCollector(observerId,
                new LocalApplicationRegistry(Path.Combine(directory.FullName, "applications.json")),
                local.Store, TimeSpan.FromSeconds(1), Read, output, TextWriter.Null);
            for (var i = 0; i < 6; i++) collector.Poll();
            collector.Stop();
            var sender = new OutboxSender(local.Store, client, TextWriter.Null);
            await sender.SynchronizeAsync(cancellationToken);
            await sender.SynchronizeAsync(cancellationToken);
            await Assert.That(local.Store.Pending().Count).IsEqualTo(0);
            var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
            var observationIds = lines.Select(line => line.GetProperty("observationId").GetGuid()).Distinct().ToArray();
            await Assert.That(observationIds.Length).IsEqualTo(3);
            await Assert.That(lines.Select(line => line.GetProperty("localApplicationId").GetGuid()).Distinct().Count()).IsEqualTo(1);
            var first = await client.GetFromJsonAsync<JsonElement>($"entities/{observationIds[0]}", cancellationToken);
            var resumed = await client.GetFromJsonAsync<JsonElement>($"entities/{observationIds[2]}", cancellationToken);
            await Assert.That(first.GetProperty("properties").GetProperty("endAt").GetString()).IsEqualTo("2026-10-11T01:00:02Z");
            await Assert.That(resumed.GetProperty("properties").GetProperty("startAt").GetString()).IsEqualTo("2026-10-11T01:00:04Z");
            await Assert.That(resumed.GetProperty("properties").GetProperty("endAt").GetString()).IsEqualTo("2026-10-11T01:00:05Z");
            var contentId = first.GetProperty("references").GetProperty("contentId").GetGuid();
            var content = await client.GetFromJsonAsync<JsonElement>($"entities/{contentId}", cancellationToken);
            await Assert.That(content.GetProperty("properties").GetProperty("name").GetString()).IsEqualTo("App");
        }
        finally { directory.Delete(recursive: true); }
    }
}
