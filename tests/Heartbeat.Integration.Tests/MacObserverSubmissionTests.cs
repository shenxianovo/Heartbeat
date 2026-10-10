using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Heartbeat.Application.Entities;
using Heartbeat.Core;
using Heartbeat.Observers.ForegroundState;
using Heartbeat.Testing;
using Microsoft.Extensions.DependencyInjection;

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
        var first = ForegroundCapture.Create(observerId,
            new ForegroundApplicationReading("com.example.app", "应用 A", null, time, "Asia/Shanghai"));
        var second = ForegroundCapture.Create(observerId,
            new ForegroundApplicationReading("com.example.app", "应用 B", "/Applications/B.app", time.AddSeconds(1),
                "Asia/Shanghai"));
        foreach (var capture in new[] { first, first, second })
            await capture.SubmitAndVerifyAsync(client, TextWriter.Null, cancellationToken);
        await Assert.That(first.Content.Id == second.Content.Id).IsFalse();
        await Assert.That(first.Observation.Id == second.Observation.Id).IsFalse();
        await Assert.That(first.Observation.ObserverId).IsEqualTo(second.Observation.ObserverId);
        await Assert.That(first.Observation.SchemaId).IsEqualTo(second.Observation.SchemaId);

        var oldContent = await client.GetFromJsonAsync<JsonElement>($"entities/{first.Content.Id.Value}", cancellationToken);
        await Assert.That(oldContent.GetProperty("properties").GetProperty("name").GetString()).IsEqualTo("应用 A");
        await Assert.That(oldContent.GetProperty("properties").GetProperty("executablePath").ValueKind)
            .IsEqualTo(JsonValueKind.Null);
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
        var capture = ForegroundCapture.Create(EntityId.New(),
            new ForegroundApplicationReading(null, null, null, DateTimeOffset.UtcNow, null));
        using var progress = new StringWriter(CultureInfo.InvariantCulture);
        await capture.SubmitAndVerifyAsync(client, progress, cancellationToken);
        await Assert.That(progress.ToString().Contains("PUT entities/foreground-application-readings/", StringComparison.Ordinal)).IsTrue();
        await Assert.That(progress.ToString().Split("GET ", StringSplitOptions.None).Length).IsEqualTo(5);
    }
}
