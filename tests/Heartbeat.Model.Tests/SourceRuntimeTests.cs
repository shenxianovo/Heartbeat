using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Heartbeat.Observers.Sources;
using Heartbeat.Observers.Sources.Local;

namespace Heartbeat.Model.Tests;

public sealed class SourceRuntimeTests
{
    private static readonly SourceSchema Schema = new(Guid.Parse("019a1280-0000-7000-8000-000000001001"), "runtime-test-records", "Runtime test",
        SourceFields.WithIdentity(new { value = SourceFields.String() }));
    private static SourceRecord Record(string evidence = "revision-a", string value = "first") => new("record", "native-1", evidence, Schema,
        SourceFields.Of(new { value }), new Dictionary<string, SourceReference>());
    private static string PathForTest() => Path.Combine(Path.GetTempPath(), "heartbeat-source-tests-" + Guid.NewGuid().ToString("N"), "state.sqlite");

    [Test]
    public async Task ReplayRestartVersionsAndIndependentObservationsHaveDistinctMeanings()
    {
        var path = PathForTest();
        try
        {
            SourceReceipt first; Guid observer;
            using (var store = new SourceStore(path)) { observer = store.ObserverId; first = store.Enqueue("account-a", Record()); }
            using var reopened = new SourceStore(path);
            var replay = reopened.Enqueue("account-a", Record());
            var independent = reopened.Enqueue("account-a", Record("independent-observation"));
            var changed = reopened.Enqueue("account-a", Record("revision-b", "edited"));
            var otherAccount = reopened.Enqueue("account-b", Record());
            await Assert.That(reopened.ObserverId).IsEqualTo(observer);
            await Assert.That(replay.ObservationId).IsEqualTo(first.ObservationId);
            await Assert.That(replay.Replayed).IsTrue();
            await Assert.That(independent.ContentId).IsEqualTo(first.ContentId);
            await Assert.That(independent.ObservationId == first.ObservationId).IsFalse();
            await Assert.That(changed.SourceEntityId).IsEqualTo(first.SourceEntityId);
            await Assert.That(changed.ContentId == first.ContentId).IsFalse();
            await Assert.That(otherAccount.SourceEntityId == first.SourceEntityId).IsFalse();
            await Assert.That(reopened.Pending().Count).IsEqualTo(4);
            var historic = reopened.Pending().Single(p => p.ObservationId == first.ObservationId).Entities.Single(e => e.Id == first.ContentId);
            await Assert.That(historic.Body.GetProperty("properties").GetProperty("value").GetString()).IsEqualTo("first");
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [Test]
    public async Task ConflictingEvidenceAndBatchFailureLeaveNoPartialOutbox()
    {
        var path = PathForTest();
        try
        {
            using var store = new SourceStore(path);
            var first = store.Enqueue("scope", Record());
            var rejected = false;
            try { store.EnqueueBatch("scope", [Record("new"), Record(value: "contradiction")]); }
            catch (InvalidDataException) { rejected = true; }
            await Assert.That(rejected).IsTrue();
            await Assert.That(store.Pending().Count).IsEqualTo(1);
            await Assert.That(store.Pending()[0].ObservationId).IsEqualTo(first.ObservationId);
            var cancelled = new CancellationToken(true);
            try { store.EnqueueBatch("scope", [Record("cancelled")], cancelled); }
            catch (OperationCanceledException) { }
            await Assert.That(store.Pending().Count).IsEqualTo(1);
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [Test]
    public async Task SchemaMeaningChangeRequiresNewIdentityAndResource()
    {
        var path = PathForTest();
        try
        {
            using var store = new SourceStore(path);
            store.Enqueue("scope", Record());
            var rejected = false;
            try { store.Enqueue("scope", Record("new") with { Schema = Schema with { ResourceName = "renamed-resource" } }); }
            catch (InvalidDataException) { rejected = true; }
            await Assert.That(rejected).IsTrue();
            await Assert.That(store.Pending().Count).IsEqualTo(1);
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [Test]
    public async Task SenderRequiresLaterReadbackAndRetainsMismatchingRemoteEvidence()
    {
        var path = PathForTest();
        try
        {
            using var store = new SourceStore(path);
            var receipt = store.Enqueue("scope", Record());
            using var handler = new MemoryServer();
            using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
            var sender = new SourceSender(store, client);
            await sender.SynchronizeAsync(CancellationToken.None);
            await Assert.That(store.Pending().Count).IsEqualTo(1);
            await sender.SynchronizeAsync(CancellationToken.None);
            await Assert.That(store.Pending().Count).IsEqualTo(0);
            store.Enqueue("scope", Record()); // Explicit replay reconciles same IDs, even after acknowledgement.
            handler.Values[receipt.ContentId] = SourceFields.Of(new { category = "entity", references = new { }, properties = new { value = "remote conflict" }, schemaId = Schema.Id });
            var puts = handler.Puts;
            await sender.SynchronizeAsync(CancellationToken.None);
            await Assert.That(store.Pending().Count).IsEqualTo(1);
            await Assert.That(store.Pending()[0].LastError is not null).IsTrue();
            await Assert.That(handler.Puts).IsEqualTo(puts);
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [Test]
    public async Task LostResponseIsRecoveredFromReadbackWithoutNewIds()
    {
        var path = PathForTest();
        try
        {
            using var store = new SourceStore(path);
            var original = store.Enqueue("scope", Record());
            using var handler = new MemoryServer { LoseFirstResponse = true };
            using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
            var sender = new SourceSender(store, client);
            for (var i = 0; i < 3; i++) await sender.SynchronizeAsync(CancellationToken.None);
            await Assert.That(store.Pending().Count).IsEqualTo(0);
            await Assert.That(handler.Values.ContainsKey(original.ObservationId)).IsTrue();
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [Test]
    public async Task SameCaptureKeyKeepsItsInstantAndSecondInstanceCannotOwnTheState()
    {
        var path = PathForTest();
        try
        {
            using var store = new SourceStore(path);
            var first = store.CaptureTime("scope", "capture");
            await Assert.That(store.CaptureTime("scope", "capture")).IsEqualTo(first);
            var rejected = false;
            try { using var another = new SourceStore(path); }
            catch (IOException) { rejected = true; }
            await Assert.That(rejected).IsTrue();
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [Test]
    public async Task ProcStatHandlesParenthesesInProcessNameAndReadsActualSelf()
    {
        var suffix = string.Join(' ', Enumerable.Range(0, 20).Select(i => i == 0 ? "R" : i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        var parsed = ProcessSource.ParseStat("123 (odd ) process) " + suffix);
        await Assert.That(parsed.Name).IsEqualTo("odd ) process");
        await Assert.That(parsed.StartTicks).IsEqualTo("19");
        if (!OperatingSystem.IsLinux()) return;
        var records = new List<SourceRecord>();
        await foreach (var item in new ProcessSource().ReadAsync(new("self", "test-host", "sample", DateTimeOffset.UtcNow), CancellationToken.None)) records.Add(item);
        await Assert.That(records.Count).IsEqualTo(1);
        await Assert.That(records[0].Properties.GetProperty("pid").GetInt32()).IsEqualTo(Environment.ProcessId);
        await Assert.That(records[0].Properties.GetProperty("namespacePids").GetArrayLength() > 0).IsTrue();
    }

    [Test]
    public async Task SchemaDisplayRenameIsAllowedButStructureAndMalformedReadbacksAreRejected()
    {
        var body = SourceFields.Of(new { references = new { }, properties = new { name = "original", resourceName = "test-records", fields = Schema.Fields } });
        var expected = new SourceSubmission(Schema.Id, "entity_schema", "schemas", body);
        var renamed = SourceFields.Of(new { category = "entity_schema", references = new { }, properties = new { name = "renamed", resourceName = "test-records", fields = Schema.Fields } });
        await Assert.That(SourceSender.Matches(expected, renamed)).IsTrue();
        var changed = SourceFields.Of(new { category = "entity_schema", references = new { }, properties = new { name = "renamed", resourceName = "other-records", fields = Schema.Fields } });
        await Assert.That(SourceSender.Matches(expected, changed)).IsFalse();
        await Assert.That(SourceSender.Matches(expected, SourceFields.Of(new { category = 4, references = new { }, properties = new { } }))).IsFalse();
        var changedFields = SourceFields.Of(new { category = "entity_schema", references = new { }, properties = new { name = "renamed", resourceName = "test-records", fields = new { } } });
        await Assert.That(SourceSender.Matches(expected, changedFields)).IsFalse();
    }

    private sealed class MemoryServer : HttpMessageHandler
    {
        internal Dictionary<Guid, JsonElement> Values { get; } = [];
        internal bool LoseFirstResponse { get; set; }
        internal int Puts { get; private set; }
        private readonly Dictionary<string, Guid> _schemas = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var parts = request.RequestUri!.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var id = Guid.Parse(parts[^1]);
            if (request.Method == HttpMethod.Get) return Values.TryGetValue(id, out var value)
                ? new(HttpStatusCode.OK) { Content = JsonContent.Create(value) } : new(HttpStatusCode.NotFound);
            Puts++;
            var body = await request.Content!.ReadFromJsonAsync<JsonElement>(cancellationToken);
            var resource = parts[^2];
            var category = resource switch { "schemas" => "entity_schema", "observers" => "observer", "observations" => "observation", _ => "entity" };
            if (resource == "schemas") _schemas[body.GetProperty("properties").GetProperty("resourceName").GetString()!] = id;
            Values[id] = SourceFields.Of(new { category, references = body.GetProperty("references"), properties = body.GetProperty("properties"),
                schemaId = category == "entity" ? (Guid?)_schemas[resource] : null });
            if (LoseFirstResponse) { LoseFirstResponse = false; throw new HttpRequestException("Response lost after server save."); }
            return new(HttpStatusCode.Created);
        }
    }
}
