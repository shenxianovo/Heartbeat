using System.Net;
using System.Text.Json;
using Orisumi.Core;
using Orisumi.Observer.Runtime;
using Orisumi.Observer.Sdk;

namespace Orisumi.Integration.Tests;

public sealed class RuntimeSubmissionTests
{
    private static readonly JsonElement Empty = JsonSerializer.SerializeToElement(new { });

    [Test, Property("caseId", "runtime-handoff")]
    public async Task BatchIsOneSubmissionWithBoundReferencesAndNamedResults()
    {
        await using var database = await DatabaseFixture.CreateAsync();
        await using var factory = new OrisumiApiFactory(database.ConnectionString);
        using var recording = new MemoryBackend(factory.Server.CreateHandler());
        using var http = recording.CreateClient();
        var owner = EntityId.New();
        var session = new RuntimeObserverSession(http, owner);
        var schema = await RegisterAsync(session, owner);
        var content = EntityId.New();
        var observation = EntityId.New();
        var result = await session.RequestAsync("commit", JsonLines.Json(new { operations = new object[]
        {
            new { op = "write", entityId = content.Value, category = "business", schemaVersionId = schema.Value, previousVersionId = (Guid?)null, content = Empty, @as = "content" },
            new { op = "reference", entityId = owner.Value, @as = "observer" },
            new { op = "write", entityId = observation.Value, category = "observation", previousVersionId = (Guid?)null,
                content = new { observerVersionId = Guid.Empty, contentVersionId = Guid.Empty, inputSetVersionId = (Guid?)null, startAt = (string?)null, endAt = (string?)null, timeZone = (string?)null },
                bindings = new Dictionary<string, string> { ["/observerVersionId"] = "observer", ["/contentVersionId"] = "content" }, @as = "observation" },
        } }));
        var results = result.GetProperty("results");
        await Assert.That(recording.Posts[^1].Length).IsEqualTo(2);
        using var read = await http.GetAsync($"entities/{observation.Value}");
        var saved = await EntityApiTests.Json(read);
        await Assert.That(saved.GetProperty("versionId").GetGuid()).IsEqualTo(results.GetProperty("observation").GetGuid());
        await Assert.That(saved.GetProperty("content").GetProperty("contentVersionId").GetGuid()).IsEqualTo(results.GetProperty("content").GetGuid());
        await Assert.That(saved.GetProperty("content").GetProperty("observerVersionId").GetGuid()).IsEqualTo(results.GetProperty("observer").GetGuid());
    }

    [Test, Property("caseId", "runtime-handoff")]
    public async Task OmittedPreviousVersionFollowsTheCurrentVersionAndIfAbsentKeepsIt()
    {
        await using var database = await DatabaseFixture.CreateAsync();
        await using var factory = new OrisumiApiFactory(database.ConnectionString);
        using var http = factory.CreateClient();
        var owner = EntityId.New();
        var session = new RuntimeObserverSession(http, owner);
        var schema = await RegisterAsync(session, owner);
        var entity = EntityId.New();
        object Write(bool? ifAbsent = null) => ifAbsent is null
            ? new { op = "write", entityId = entity.Value, category = "business", schemaVersionId = schema.Value, content = Empty, @as = "version" }
            : new { op = "write", entityId = entity.Value, category = "business", schemaVersionId = schema.Value, content = Empty, ifAbsent, @as = "version" };
        Guid Commit(params object[] operations) => session.RequestAsync("commit", JsonLines.Json(new { operations })).GetAwaiter().GetResult()
            .GetProperty("results").GetProperty("version").GetGuid();
        var first = Commit(Write());
        var second = Commit(Write());
        var kept = Commit(Write(ifAbsent: true));
        await Assert.That(kept).IsEqualTo(second);
        using var chain = await http.GetAsync($"entities/{entity.Value}/versions");
        var versions = (await EntityApiTests.Json(chain)).GetProperty("versions").EnumerateArray().Select(VersionJson.ReadSaved).ToArray();
        await Assert.That(versions.Select(version => version.VersionId.Value).SequenceEqual(new[] { first, second })).IsTrue();
        await Assert.That(versions[1].PreviousVersionId!.Value.Value).IsEqualTo(first);
    }

    [Test, Property("caseId", "runtime-handoff")]
    public async Task RejectedSubmissionSavesNothingAndReportsTheRule()
    {
        await using var database = await DatabaseFixture.CreateAsync();
        await using var factory = new OrisumiApiFactory(database.ConnectionString);
        using var http = factory.CreateClient();
        var owner = EntityId.New();
        var session = new RuntimeObserverSession(http, owner);
        var schema = await RegisterAsync(session, owner);
        var existing = EntityId.New();
        var fresh = EntityId.New();
        object First(EntityId id) => new { op = "write", entityId = id.Value, category = "business", schemaVersionId = schema.Value, previousVersionId = (Guid?)null, content = Empty };
        await session.RequestAsync("commit", JsonLines.Json(new { operations = new[] { First(existing) } }));
        var rejection = await Assert.That(async () => await session.RequestAsync("commit", JsonLines.Json(new { operations = new[] { First(fresh), First(existing) } })))
            .Throws<SubmissionRejectedException>();
        await Assert.That(rejection!.Rule).IsEqualTo("VERSION-6");
        using var read = await http.GetAsync($"entities/{fresh.Value}");
        await Assert.That(read.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test, Property("caseId", "runtime-handoff")]
    public async Task UnavailableBackendFailsTheCommit()
    {
        using var backend = new MemoryBackend();
        using var http = backend.CreateClient();
        var session = new RuntimeObserverSession(http, EntityId.New());
        backend.Offline = true;
        var operations = new[] { new { op = "write", entityId = EntityId.New().Value, category = "entity_schema", previousVersionId = (Guid?)null,
            content = new { displayName = "Example", description = "Example", resourceName = "examples", fields = new { } } } };
        await Assert.That(async () => await session.RequestAsync("commit", JsonLines.Json(new { operations }))).Throws<HttpRequestException>();
        await Assert.That(backend.Posts.Count).IsEqualTo(0);
    }

    [Test, Property("caseId", "runtime-registration")]
    public async Task SameSchemaContentReusesItsVersionAndNewContentExtendsTheChain()
    {
        await using var database = await DatabaseFixture.CreateAsync();
        await using var factory = new OrisumiApiFactory(database.ConnectionString);
        using var http = factory.CreateClient();
        var first = EntityApiTests.Schema();
        var second = first with { VersionId = VersionId.New(), PreviousVersionId = first.VersionId,
            Content = new EntitySchema("Items", "Second", "items", JsonSerializer.SerializeToElement(new { })) };
        using var saved = await EntityApiTests.Submit(http, first, second);
        await Assert.That(saved.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var session = new RuntimeObserverSession(http, EntityId.New());
        async Task<Guid> Register(EntitySchema schema) => (await session.RequestAsync("registerSchema",
            JsonLines.Json(new { entityId = first.EntityId.Value, content = VersionJson.Content(schema) }))).GetProperty("versionId").GetGuid();
        await Assert.That(await Register((EntitySchema)first.Content)).IsEqualTo(first.VersionId.Value);
        var third = await Register(new EntitySchema("Items", "Third", "items", JsonSerializer.SerializeToElement(new { })));
        using var chain = await http.GetAsync($"entities/{first.EntityId.Value}/versions");
        var versions = (await EntityApiTests.Json(chain)).GetProperty("versions").EnumerateArray().Select(VersionJson.ReadSaved).ToArray();
        await Assert.That(versions.Select(v => v.VersionId.Value).SequenceEqual(new[] { first.VersionId.Value, second.VersionId.Value, third })).IsTrue();
        await Assert.That(versions[2].PreviousVersionId).IsEqualTo(second.VersionId);
    }

    [Test, Property("caseId", "runtime-registration")]
    public async Task ChangedInstanceInformationFormsTheNextVersion()
    {
        await using var database = await DatabaseFixture.CreateAsync();
        await using var factory = new OrisumiApiFactory(database.ConnectionString);
        using var http = factory.CreateClient();
        var owner = EntityId.New();
        var schema = EntityApiTests.Schema(fields: """{"programVersion":{"type":"string","description":"Test field value.","required":true,"nullable":false}}""");
        var session = new RuntimeObserverSession(http, owner);
        var schemaVersion = (await session.RequestAsync("registerSchema", JsonLines.Json(new { entityId = schema.EntityId.Value, content = VersionJson.Content(schema.Content) })))
            .GetProperty("versionId").GetGuid();
        async Task<Guid> Register(string version) => (await new RuntimeObserverSession(http, owner).RequestAsync("registerInstance",
            JsonLines.Json(new { schemaVersionId = schemaVersion, content = new { programVersion = version } }))).GetProperty("versionId").GetGuid();
        var first = await Register("1");
        await Assert.That(await Register("1")).IsEqualTo(first);
        var second = await Register("2");
        using var chain = await http.GetAsync($"entities/{owner.Value}/versions");
        var versions = (await EntityApiTests.Json(chain)).GetProperty("versions").EnumerateArray().Select(VersionJson.ReadSaved).ToArray();
        await Assert.That(versions.Select(v => v.VersionId.Value).SequenceEqual(new[] { first, second })).IsTrue();
        await Assert.That(versions[1].PreviousVersionId!.Value.Value).IsEqualTo(first);
    }

    private static async Task<VersionId> RegisterAsync(RuntimeObserverSession session, EntityId owner)
    {
        var schema = EntityApiTests.Schema();
        var schemaVersion = (await session.RequestAsync("registerSchema", JsonLines.Json(new { entityId = schema.EntityId.Value, content = VersionJson.Content(schema.Content) })))
            .GetProperty("versionId").GetGuid();
        await session.RequestAsync("registerInstance", JsonLines.Json(new { schemaVersionId = schemaVersion, content = new { } }));
        return new(schemaVersion);
    }
}
