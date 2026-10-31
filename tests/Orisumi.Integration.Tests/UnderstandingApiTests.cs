using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Orisumi.Core;
using Orisumi.Observer.Sdk;
using Orisumi.Testing;

namespace Orisumi.Integration.Tests;

public sealed class UnderstandingApiTests
{
    [Test, Property("caseId", "api-understanding")]
    [Arguments("deny", false, false)]
    [Arguments("deny", true, true)]
    [Arguments("supersede", false, false)]
    [Arguments("supersede", false, true)]
    public async Task ReviewsDetermineCurrentUnderstanding(string verdict, bool partial, bool retained)
    {
        await using var f = await Fixture.Create();
        var old = await f.Writer.WriteAsync(f.Schema, new { name = "old" }, "old");
        var next = await f.Writer.WriteAsync(f.Schema, new { name = "new" }, "new");
        if (verdict == "supersede" && retained) await f.Person.WriteAsync(new(CommonSchemas.Review,
            new { targetVersionId = old.VersionId.Value, verdict = "confirm", text = (string?)null }, "stated"));
        await (verdict == "deny" ? f.Person : f.Model).WriteAsync(new(CommonSchemas.Review,
            new { targetVersionId = old.VersionId.Value, verdict, text = (string?)null }, verdict == "deny" ? "stated" : "inferred",
            Time: partial ? FactTime.Point(DateTimeOffset.UtcNow, null) : null));
        var current = await f.Model.ListAsync("observations?resourceName=items&standing=current");
        await Assert.That(current.Any(v => v.GetProperty("entityId").GetGuid() == old.EntityId.Value)).IsEqualTo(retained);
        await Assert.That(current.Any(v => v.GetProperty("entityId").GetGuid() == next.EntityId.Value)).IsTrue();
        await Assert.That((await f.Model.ListAsync("observations?resourceName=items&standing=all")).Count).IsEqualTo(2);
    }

    [Test, Property("caseId", "api-understanding"), Property("caseId", "api-lists"), Property("caseId", "api-deletion")]
    public async Task ThingNamesPreferPeopleThenLatestAndDeletionRetainsReferences()
    {
        await using var f = await Fixture.Create();
        var type = await f.Writer.DeclareAsync("objects", kind: "thing");
        var target = Ref.Recognize(type.Value, new(null, "source", "object"));
        var source = await f.Writer.WriteAsync(new(CommonSchemas.Name), new { thingId = target, name = "source" });
        var id = ((Observation)source.Content).Fields.GetProperty("thingId").GetGuid();
        var human = await f.Person.WriteAsync(new(CommonSchemas.Name, new { thingId = id, name = "user" }, "stated"));
        await f.Model.WriteAsync(new(CommonSchemas.Name, new { thingId = id, name = "model" }, "inferred"));
        var thing = await f.Model.EntityAsync(id);
        await Assert.That(thing.GetProperty("category").GetString()).IsEqualTo("thing");
        await Assert.That(thing.TryGetProperty("versionId", out _)).IsFalse();
        await Assert.That(thing.GetProperty("name").GetString()).IsEqualTo("user");
        await f.Person.WriteAsync(new(CommonSchemas.Review, new { targetVersionId = human!.Version!.Value.GetProperty("versionId").GetGuid(), verdict = "deny", text = (string?)null }, "stated"));
        var things = await f.Model.ListAsync("things?resourceName=objects&limit=1");
        await Assert.That(things.Single().GetProperty("name").GetString()).IsEqualTo("model");
        using var batch = await f.Http.PostAsJsonAsync("things/read", new { entityIds = new[] { id, Guid.CreateVersion7(), id } });
        var read = (await WriteClient.JsonAsync(batch)).GetProperty("results");
        await Assert.That(read.EnumerateArray().Select(v => v.GetProperty("status").GetString()).SequenceEqual(["found", "not_found", "found"])).IsTrue();
        using var deleted = await f.Http.DeleteAsync($"entities/{id}"); deleted.EnsureSuccessStatusCode();
        var referrers = await f.Model.GetAsync($"entities/{id}/referrers");
        await Assert.That(referrers.GetProperty("referrers").GetArrayLength()).IsEqualTo(3);
        var reference = referrers.GetProperty("referrers")[0].GetProperty("references")[0];
        await Assert.That(reference.GetProperty("targetEntityId").GetGuid()).IsEqualTo(id);
        await Assert.That(reference.GetProperty("targetVersionId").ValueKind).IsEqualTo(JsonValueKind.Null);
        var rejected = await f.Writer.SendAsync([WriteClient.Write(new(CommonSchemas.Name), new { thingId = id, name = "gone" })]);
        await Assert.That(rejected.GetProperty("results")[0].GetProperty("error").GetProperty("rule").GetString()).IsEqualTo("ENTITY-6");
        var recreated = await f.Writer.WriteAsync(new(CommonSchemas.Name), new { thingId = target, name = "again" });
        await Assert.That(((Observation)recreated.Content).Fields.GetProperty("thingId").GetGuid() == id).IsFalse();
        using var gone = await f.Http.GetAsync($"entities/{id}");
        await Assert.That(gone.StatusCode).IsEqualTo(HttpStatusCode.Gone);
    }

    [Test, Property("caseId", "api-understanding"), Property("caseId", "api-referrers")]
    public async Task DeniedInputRemainsTraceableThroughInputSetAndReferrers()
    {
        await using var f = await Fixture.Create();
        var evidence = await f.Writer.WriteAsync(f.Schema, new { name = "evidence" });
        var conclusion = await f.Writer.WriteAsync(f.Schema, new { name = "conclusion" }, inputs: [evidence.VersionId]);
        await f.Person.WriteAsync(new(CommonSchemas.Review, new { targetVersionId = evidence.VersionId.Value, verdict = "deny", text = "incorrect" }, "stated"));
        var input = VersionJson.ReadSaved(await f.Model.VersionAsync(((Observation)conclusion.Content).InputSetVersionId!.Value.Value));
        await Assert.That(((InputSet)input.Content).Members.Single()).IsEqualTo(evidence.VersionId);
        var referrers = await f.Model.GetAsync($"entities/{evidence.EntityId.Value}/referrers?resourceName=reviews");
        await Assert.That(referrers.GetProperty("referrers")[0].GetProperty("version").GetProperty("content").GetProperty("verdict").GetString()).IsEqualTo("deny");
        var author = await f.Model.GetAsync($"entities/{f.Writer.ObserverId.Value}/referrers?path=/observerId");
        await Assert.That(author.GetProperty("referrers").GetArrayLength()).IsEqualTo(3);
    }

    [Test, Property("caseId", "api-writes"), Property("caseId", "api-reading"), Property("caseId", "api-schema-fields")]
    public async Task FormationDescriptionsAuthorsAndImmutableContentFollowTheContract()
    {
        await using var f = await Fixture.Create();
        var schema = await f.Writer.DeclareAsync("immutable-items", WriteApiTests.NameFields, immutable: true);
        var first = await f.Writer.WriteAsync(schema, new { name = "first" }, "item");
        await Assert.That(((Observation)first.Content).ObserverVersionId).IsEqualTo(f.Writer.Observer.VersionId);
        await Assert.That(((Observation)f.Writer.Observer.Content).ObserverVersionId).IsNull();
        await Assert.That(((Observation)first.Content).StartAt).IsNull();
        var errors = (await f.Writer.SendAsync([
            WriteClient.Write(schema, new { name = "changed" }, "item"),
            new { schemaId = schema.Value, formation = "invalid", content = new { name = "bad" } }])).GetProperty("results");
        await Assert.That(errors[0].GetProperty("error").GetProperty("rule").GetString()).IsEqualTo("SCHEMA-18");
        await Assert.That(errors[1].GetProperty("error").GetProperty("rule").GetString()).IsEqualTo("OBSERVATION-11");
        using var missing = await f.Http.PostAsJsonAsync("writes", new { observer = f.Writer.ObserverReference, writes = new[] { new { schemaId = schema.Value, content = new { name = "missing formation" } } } });
        await Assert.That(missing.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        var time = await f.Writer.WriteAsync(schema, new { name = "first" }, "item", time: WriteApiTests.UnknownTime);
        await Assert.That(time.VersionId).IsEqualTo(first.VersionId);
        var extended = await f.Writer.WriteAsync(schema, new { name = "first" }, "item", time: new { startAt = "2026-10-31T01:00:00Z", endAt = (string?)null, timeZone = (string?)null });
        await Assert.That(extended.PreviousVersionId).IsEqualTo(first.VersionId);
        var other = new WriteClient(f.Http); await other.InitializeAsync();
        await other.DeclareAsync("immutable-items", WriteApiTests.NameFields, schema, immutable: false);
        var same = await other.WriteAsync(schema, new { name = "first" }, "item");
        await Assert.That(same.VersionId).IsEqualTo(extended.VersionId);
        var conflict = (await other.SendAsync([WriteClient.Write(schema, new { name = "other author" }, "item")])).GetProperty("results")[0];
        await Assert.That(conflict.GetProperty("error").GetProperty("rule").GetString()).IsEqualTo("OBSERVATION-10");
        var description = await f.Writer.DeclareAsync("new-descriptions", WriteApiTests.NameFields);
        await f.Writer.SendAsync([], description: new { schemaId = description.Value, content = new { name = "new parser" } });
        var corrected = await f.Writer.WriteAsync(schema, new { name = "corrected" }, "item");
        await Assert.That(corrected.PreviousVersionId).IsEqualTo(extended.VersionId);
        var filtered = await f.Model.ListAsync("observations?resourceName=immutable-items&formation=direct");
        await Assert.That(filtered.Count).IsEqualTo(1);
    }

    [Test, Property("caseId", "api-write-references"), Property("caseId", "storage-transactions")]
    public async Task NewThingsRollbackWithRejectedWritesAndExistingThingReferencesStayUnchanged()
    {
        await using var f = await Fixture.Create();
        var thingSchema = await f.Writer.DeclareAsync("objects", kind: "thing");
        var linkSchema = await f.Writer.DeclareAsync("links", """{"targetId":{"type":"reference","target":"thing","description":"Target","required":true,"nullable":false},"name":{"type":"string","description":"Name","required":true,"nullable":false}}""");
        var target = Ref.Recognize(thingSchema.Value, new(null, "source", "one"));
        var rejected = await f.Writer.SendAsync([WriteClient.Write(linkSchema, new { targetId = target, name = 1 })]);
        await Assert.That(rejected.GetProperty("results")[0].GetProperty("error").GetProperty("rule").GetString()).IsEqualTo("SCHEMA-2");
        await Assert.That((await f.Model.ListAsync("things?resourceName=objects")).Count).IsEqualTo(0);
        var first = await f.Writer.WriteAsync(linkSchema, new { targetId = target, name = "one" }, "link");
        var id = ((Observation)first.Content).Fields.GetProperty("targetId").GetGuid();
        var repeated = await f.Writer.WriteAsync(linkSchema, new { targetId = id, name = "one" }, "link");
        await Assert.That(repeated.VersionId).IsEqualTo(first.VersionId);
        var recognized = await f.Writer.WriteAsync(linkSchema, new { targetId = target, name = "one" }, "link");
        await Assert.That(recognized.VersionId).IsEqualTo(first.VersionId);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly TestDatabase db;
        private readonly OrisumiApiFactory factory;
        internal HttpClient Http { get; }
        internal WriteClient Writer { get; }
        internal DataClient Model { get; private set; } = null!;
        internal DataClient Person { get; }
        internal EntityId Schema { get; private set; }
        private Fixture(TestDatabase database)
        {
            db = database; factory = new(db.ConnectionString); Http = factory.CreateClient(); Writer = new(Http);
            Person = new(Http, Http, Ref.Recognize(Guid.Parse("019e1bbb-ec00-7000-8000-000000000006"), new(null, "orisumi.user", "owner")));
        }
        internal static async Task<Fixture> Create()
        {
            var f = new Fixture(await DatabaseFixture.CreateAsync()); await f.Writer.InitializeAsync();
            f.Model = new(f.Http, f.Http, Ref.Entity(f.Writer.ObserverId.Value));
            f.Schema = await f.Writer.DeclareAsync(fields: WriteApiTests.NameFields); return f;
        }
        public async ValueTask DisposeAsync() { Http.Dispose(); await factory.DisposeAsync(); await db.DisposeAsync(); }
    }
}
