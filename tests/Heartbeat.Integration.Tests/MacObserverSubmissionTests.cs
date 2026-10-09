using System.Globalization;
using System.Net;
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
    public async Task ReadingsAndReplayRoundTripWithoutReplacingHistory()
    {
        var cancellationToken = TestContext.Current!.Execution.CancellationToken;
        await using var factory = new HeartbeatApiFactory(_database!.ConnectionString);
        using var client = factory.CreateClient();
        var observerId = EntityId.New();
        var time = DateTimeOffset.Parse("2026-10-07T09:00:00.1234567+08:00", CultureInfo.InvariantCulture);
        var first = ForegroundCapture.Create(observerId,
            new ForegroundApplicationReading("com.example.app", "应用 A", null, time, "Asia/Shanghai"));
        await first.SubmitAndVerifyAsync(client, TextWriter.Null, cancellationToken);
        await first.SubmitAndVerifyAsync(client, TextWriter.Null, cancellationToken);
        var second = ForegroundCapture.Create(observerId,
            new ForegroundApplicationReading("com.example.app", "应用 B", "/Applications/B.app", time.AddSeconds(1),
                "Asia/Shanghai"));
        await second.SubmitAndVerifyAsync(client, TextWriter.Null, cancellationToken);
        await Assert.That(first.Data.Id == second.Data.Id).IsFalse();
        await Assert.That(first.Observation.Id == second.Observation.Id).IsFalse();
        await Assert.That(first.Observation.ObserverId).IsEqualTo(second.Observation.ObserverId);
        await Assert.That(first.Observation.SchemaId).IsEqualTo(second.Observation.SchemaId);

        var oldData = await client.GetFromJsonAsync<JsonElement>($"entities/{first.Data.Id.Value}", cancellationToken);
        await Assert.That(oldData.GetProperty("entity").GetProperty("name").GetString()).IsEqualTo("应用 A");
        await Assert.That(oldData.GetProperty("entity").GetProperty("executablePath").ValueKind)
            .IsEqualTo(JsonValueKind.Null);
        var observation = await client.GetFromJsonAsync<JsonElement>(
            $"entities/{first.Observation.Id.Value}", cancellationToken);
        var body = observation.GetProperty("entity");
        await Assert.That(body.GetProperty("startAt").GetString()).IsEqualTo("2026-10-07T01:00:00.123456Z");
        await Assert.That(body.GetProperty("endAt").GetString()).IsEqualTo(body.GetProperty("startAt").GetString());
    }

    [Test]
    public async Task FailedObservationSaveReportsItsStepAndPreservesSuccessfulSaves()
    {
        var cancellationToken = TestContext.Current!.Execution.CancellationToken;
        await using var factory = new HeartbeatApiFactory(_database!.ConnectionString);
        using var client = factory.CreateClient();
        var capture = ForegroundCapture.Create(EntityId.New(),
            new ForegroundApplicationReading(null, null, null, DateTimeOffset.UtcNow, null));
        using var occupied = await client.PutAsJsonAsync($"entities/{capture.Observation.Id.Value}",
            new { original = true }, cancellationToken);
        occupied.EnsureSuccessStatusCode();
        using var progress = new StringWriter(CultureInfo.InvariantCulture);
        HttpRequestException? failure = null;
        try
        {
            await capture.SubmitAndVerifyAsync(client, progress, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            failure = exception;
        }
        await Assert.That(failure?.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(failure is not null
            && failure.Message.Contains(capture.Observation.Id.Value.ToString(), StringComparison.Ordinal)).IsTrue();
        await Assert.That(progress.ToString().Contains("GET ", StringComparison.Ordinal)).IsFalse();
        var data = await client.GetFromJsonAsync<JsonElement>($"entities/{capture.Data.Id.Value}", cancellationToken);
        await Assert.That(data.GetProperty("category").GetString()).IsEqualTo("entity");
        await Assert.That(data.GetProperty("entity").GetProperty("name").ValueKind).IsEqualTo(JsonValueKind.Null);
        var original = await client.GetFromJsonAsync<JsonElement>(
            $"entities/{capture.Observation.Id.Value}", cancellationToken);
        await Assert.That(original.GetProperty("entity").GetProperty("original").GetBoolean()).IsTrue();
    }
}
