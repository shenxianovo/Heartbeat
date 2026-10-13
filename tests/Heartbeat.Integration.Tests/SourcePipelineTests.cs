using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Heartbeat.Observers.Sources;
using Heartbeat.Observers.Sources.Communication;
using Heartbeat.Observers.Sources.Gmail;
using Heartbeat.Observers.Sources.Local;
using Heartbeat.Observers.Sources.PhysicalCalendar;
using Heartbeat.Observers.Sources.Telemetry;
using Heartbeat.Testing;

namespace Heartbeat.Integration.Tests;

public sealed class SourcePipelineTests
{
    private TestDatabase? _database;
    [Before(Test)]
    public async Task CreateDatabaseAsync(CancellationToken cancellationToken) =>
        _database = await PostgresAssemblyHooks.Instance.CreateDatabaseAsync(cancellationToken);
    [After(Test)]
    public async Task DeleteDatabaseAsync() { if (_database is not null) await _database.DisposeAsync(); }

    public static IEnumerable<(string Source, string Fixture)> Imports()
    {
        yield return ("graph", "Communication/graph-delta.json");
        yield return ("slack", "Communication/slack-create.json");
        yield return ("slack", "Communication/slack-edit.json");
        yield return ("slack", "Communication/slack-delete.json");
        yield return ("otlp", "Telemetry/otlp-traces.json");
        yield return ("otlp", "Telemetry/otlp-logs.json");
        yield return ("otlp", "Telemetry/otlp-metrics.json");
        yield return ("cloud", "Telemetry/cloudevents.json");
        yield return ("cdc", "Telemetry/debezium.json");
        yield return ("gmail", "Gmail/message.json");
        yield return ("draft", "Gmail/draft-before.json");
        yield return ("draft", "Gmail/draft-after.json");
        yield return ("history", "Gmail/history.json");
        yield return ("ics", "PhysicalCalendar/calendar.ics");
        yield return ("gpx", "PhysicalCalendar/track.gpx");
    }

    [Test]
    [MethodDataSource(nameof(Imports))]
    public async Task NativeImportSurvivesSqliteRestartAndApiReadback(string source, string fixture)
    {
        IRecordSource adapter = source switch
        {
            "graph" => new GraphMessagesSource(), "slack" => new SlackEventsSource(),
            "otlp" => new OtlpJsonSource(), "cloud" => new CloudEventsJsonSource(), "cdc" => new DebeziumJsonSource(),
            "gmail" => new GmailMessagesSource(), "draft" => new GmailDraftsSource(), "history" => new GmailHistorySource(),
            "ics" => new ICalendarSource(), "gpx" => new GpxSource(), _ => throw new ArgumentException("Unknown adapter."),
        };
        var ct = TestContext.Current!.Execution.CancellationToken;
        var records = new List<SourceRecord>();
        await foreach (var record in adapter.ReadAsync(new(Path.Combine(AppContext.BaseDirectory, "NativeFixtures", fixture), "fixture-account", "capture-one"), ct)) records.Add(record);
        await Assert.That(records.Count > 0).IsTrue();
        await VerifyPipeline(records, ct);
    }

    [Test]
    public async Task LiveProcessCaptureReachesActualApi()
    {
        if (!OperatingSystem.IsLinux()) return;
        var ct = TestContext.Current!.Execution.CancellationToken;
        var records = new List<SourceRecord>();
        await foreach (var record in new ProcessSource().ReadAsync(new("self", "test-machine", "capture", DateTimeOffset.UtcNow), ct)) records.Add(record);
        await VerifyPipeline(records, ct);
    }

    [Test]
    [Arguments("git")]
    [Arguments("file")]
    public async Task LiveLocalCaptureReachesActualApi(string source)
    {
        if (source == "file" && !OperatingSystem.IsLinux()) return;
        var ct = TestContext.Current!.Execution.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "source-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var file = Path.Combine(directory, "sample.bin");
            await File.WriteAllBytesAsync(file, new byte[] { 0, 255, 10, 42 }, ct);
            if (source == "git")
            {
                await Git("init", "--quiet"); await Git("add", "sample.bin");
                await Git("-c", "user.name=Native fixture", "-c", "user.email=fixture@example.invalid", "-c", "core.hooksPath=/dev/null", "commit", "--quiet", "-m", "Native integration fixture");
            }
            IRecordSource adapter = source == "git" ? new GitSource() : new FileSource();
            var records = new List<SourceRecord>();
            await foreach (var record in adapter.ReadAsync(new(source == "git" ? directory : file, "test-local", "capture", DateTimeOffset.UtcNow), ct)) records.Add(record);
            await VerifyPipeline(records, ct);
        }
        finally { Directory.Delete(directory, true); }

        async Task Git(params string[] arguments)
        {
            var start = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(ct); var errors = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct); await output;
            if (process.ExitCode != 0) throw new InvalidOperationException(await errors);
        }
    }

    [Test]
    public async Task OffsetAndSubMicrosecondSourceTimeKeepsExactContentAndCanonicalReadback()
    {
        const string sourceTime = "2026-10-12T12:30:01.1234567+08:00";
        var time = DateTimeOffset.Parse(sourceTime, System.Globalization.CultureInfo.InvariantCulture);
        var schema = new SourceSchema(Guid.Parse("019a1280-0000-7000-8000-000000002001"), "native-time-evidence", "Native source time",
            SourceFields.WithIdentity(new { exactTime = SourceFields.String() }));
        var record = new SourceRecord("timed-evidence", "one", "version-one", schema, SourceFields.Of(new { exactTime = sourceTime }),
            new Dictionary<string, SourceReference>(), time, time);
        await VerifyPipeline([record], TestContext.Current!.Execution.CancellationToken);
    }

    private async Task VerifyPipeline(List<SourceRecord> records, CancellationToken ct)
    {
        var directory = Path.Combine(Path.GetTempPath(), "source-pipeline-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "state.sqlite");
        try
        {
            IReadOnlyList<SourceReceipt> receipts;
            using (var initial = new SourceStore(path)) receipts = initial.EnqueueBatch("test-scope", records, ct);
            using var store = new SourceStore(path);
            var replay = store.EnqueueBatch("test-scope", records, ct);
            await Assert.That(replay.Select(r => r.ObservationId).SequenceEqual(receipts.Select(r => r.ObservationId))).IsTrue();
            await using var factory = new HeartbeatApiFactory(_database!.ConnectionString);
            using var client = factory.CreateClient();
            var sender = new SourceSender(store, client);
            var expected = store.Pending().SelectMany(p => p.Entities).DistinctBy(e => e.Id).ToList();
            await sender.SynchronizeAsync(ct);
            await sender.SynchronizeAsync(ct);
            await Assert.That(string.Join("; ", store.Pending().Select(p => p.LastError))).IsEqualTo("");
            await Assert.That(store.Pending().Count).IsEqualTo(0);
            foreach (var entity in expected)
            {
                var actual = await client.GetFromJsonAsync<JsonElement>($"entities/{entity.Id}", ct);
                await Assert.That(SourceSender.Matches(entity, actual)).IsTrue();
            }
            // Cosmetic schema names may change; immutable fields/resource and business evidence must not.
            var schema = expected.First(e => e.Category == "entity_schema");
            var schemaProperties = schema.Body.GetProperty("properties");
            using var renamed = await client.PutAsJsonAsync($"entities/schemas/{schema.Id}", new { references = new { }, properties = new
            {
                name = "User chosen display name", resourceName = schemaProperties.GetProperty("resourceName"), fields = schemaProperties.GetProperty("fields"),
            } }, ct);
            await Assert.That(renamed.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
            store.EnqueueBatch("test-scope", records, ct);
            await sender.SynchronizeAsync(ct);
            await Assert.That(store.Pending().Count).IsEqualTo(0);
            var preserved = await client.GetFromJsonAsync<JsonElement>($"entities/{schema.Id}", ct);
            await Assert.That(preserved.GetProperty("properties").GetProperty("name").GetString()).IsEqualTo("User chosen display name");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
