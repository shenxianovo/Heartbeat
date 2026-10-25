using System.Net;
using System.Text;
using System.Text.Json;
using Heartbeat.Core;
using static Heartbeat.Integration.Tests.EntityApiTests;

namespace Heartbeat.Integration.Tests;

public sealed class ReferrerApiTests
{
    // ENTITY-5, ENTITY-7, SCHEMA-9, SCHEMA-17: only declared content references count.
    [Test]
    public async Task NestedReferencesUseSavedSchemaAndRootJsonPointers()
    {
        await using var database = await DatabaseFixture.CreateAsync();
        await using var factory = new HeartbeatApiFactory(database.ConnectionString);
        using var http = factory.CreateClient();
        var schema = Schema();
        var target = Business(schema);
        var targetNext = target with { VersionId = VersionId.New(), PreviousVersionId = target.VersionId };
        var references = Schema("links", """
            {"nested":{"type":"object","required":true,"nullable":false,"fields":{
                "a~/VersionId":{"type":"reference","required":true,"nullable":false}}},
             "links":{"type":"array","required":true,"nullable":false,"items":{
                "type":"object","nullable":false,"fields":{
                    "targetVersionIds":{"type":"array","required":true,"nullable":false,
                        "items":{"type":"reference","nullable":true}}}}},
             "optionalVersionId":{"type":"reference","required":false,"nullable":true},
             "text":{"type":"string","required":true,"nullable":false}}
            """);
        var link = Business(references, $$"""
            {"nested":{"a~/VersionId":"{{target.VersionId.Value}}"},
             "links":[{"targetVersionIds":["{{targetNext.VersionId.Value}}",null,"{{target.VersionId.Value}}"]}],
             "optionalVersionId":null,"text":"{{target.VersionId.Value}}"}
            """);
        var schemaNext = references with { VersionId = VersionId.New(), PreviousVersionId = references.VersionId,
            Content = new EntitySchema("Links", "Links", "links", JsonSerializer.Deserialize<JsonElement>("{}")) };
        using var saved = await Submit(http, schema, target, targetNext, references, schemaNext, link);
        await Assert.That(saved.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var page = await Page(http, target.EntityId);
        var entry = page.GetProperty("referrers").EnumerateArray().Single();
        await Locations(entry, ("/content/nested/a~0~1VersionId", target.VersionId),
            ("/content/links/0/targetVersionIds/0", targetNext.VersionId), ("/content/links/0/targetVersionIds/2", target.VersionId));
        using var read = await http.GetAsync($"/versions/{link.VersionId.Value}");
        await Assert.That(JsonElement.DeepEquals(entry.GetProperty("version"), await Json(read))).IsTrue();
        await Assert.That(page.GetProperty("next").ValueKind).IsEqualTo(JsonValueKind.Null);
        // Neither schemaVersionId nor previousVersionId is indexed.
        await Assert.That((await Page(http, references.EntityId, "?versions=all")).GetProperty("referrers").GetArrayLength()).IsEqualTo(0);
    }

    // ENTITY-5, VERSION-7: InputSet retry order does not change saved positions.
    [Test]
    public async Task CoreReferencesKeepAllRolesAndOriginalInputPositions()
    {
        await using var database = await DatabaseFixture.CreateAsync();
        await using var factory = new HeartbeatApiFactory(database.ConnectionString);
        using var http = factory.CreateClient();
        var schema = Schema(); var target = Business(schema); var other = Business(schema);
        var inputs = new VersionSubmission(EntityId.New(), VersionId.New(), null, null, new InputSet([other.VersionId, target.VersionId]));
        var observation = new VersionSubmission(EntityId.New(), VersionId.New(), null, null,
            new Observation(target.VersionId, target.VersionId, inputs.VersionId, null, null, null));
        using var saved = await Submit(http, schema, target, other, inputs, observation);
        await Assert.That(saved.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var retry = await Submit(http, inputs with { Content = new InputSet([target.VersionId, other.VersionId]) });
        await Assert.That(retry.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var entries = (await Page(http, target.EntityId)).GetProperty("referrers").EnumerateArray().ToArray();
        await Assert.That(entries.Length).IsEqualTo(2);
        await Locations(entries.Single(e => Id(e) == inputs.VersionId.Value), ("/content/members/1", target.VersionId));
        await Locations(entries.Single(e => Id(e) == observation.VersionId.Value),
            ("/content/observerVersionId", target.VersionId), ("/content/contentVersionId", target.VersionId));
        var inputReferrer = (await Page(http, inputs.EntityId)).GetProperty("referrers").EnumerateArray().Single();
        await Locations(inputReferrer, ("/content/inputSetVersionId", inputs.VersionId));
    }

    // VERSION-5, ENTITY-7: pagination counts versions, even when each has multiple matching paths.
    [Test]
    public async Task CurrentAndAllPaginateVersionsAfterExtensionAndRetargeting()
    {
        await using var database = await DatabaseFixture.CreateAsync();
        await using var factory = new HeartbeatApiFactory(database.ConnectionString);
        using var http = factory.CreateClient();
        var schema = Schema(); var target = Business(schema); var other = Business(schema);
        var observation = new VersionSubmission(EntityId.New(), VersionId.New(), null, null,
            new Observation(target.VersionId, target.VersionId, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, null));
        var extended = observation with { VersionId = VersionId.New(), PreviousVersionId = observation.VersionId,
            Content = ((Observation)observation.Content) with { EndAt = DateTimeOffset.UnixEpoch.AddHours(1) } };
        var inputs = new VersionSubmission(EntityId.New(), VersionId.New(), null, null, new InputSet([target.VersionId]));
        var moved = inputs with { VersionId = VersionId.New(), PreviousVersionId = inputs.VersionId, Content = new InputSet([other.VersionId]) };
        using var saved = await Submit(http, schema, target, other, observation, extended, inputs, moved);
        await Assert.That(saved.StatusCode).IsEqualTo(HttpStatusCode.OK);
        foreach (var query in new[] { "", "?versions=current" })
        {
            var current = (await Page(http, target.EntityId, query)).GetProperty("referrers").EnumerateArray().Single();
            await Assert.That(Id(current)).IsEqualTo(extended.VersionId.Value);
            await Assert.That(current.GetProperty("references").GetArrayLength()).IsEqualTo(2);
        }
        var found = new List<Guid>(); string? next = null;
        do
        {
            var page = await Page(http, target.EntityId, "?versions=all&limit=1" + (next is null ? "" : "&after=" + Uri.EscapeDataString(next)));
            var entry = page.GetProperty("referrers").EnumerateArray().Single();
            found.Add(Id(entry));
            await Assert.That(entry.GetProperty("references").GetArrayLength()).IsEqualTo(Id(entry) == inputs.VersionId.Value ? 1 : 2);
            next = page.GetProperty("next").GetString();
        } while (next is not null);
        await Assert.That(found.SequenceEqual(new[] { observation.VersionId.Value, extended.VersionId.Value, inputs.VersionId.Value }.Order())).IsTrue();
    }

    // HISTORY-6, HISTORY-8: removing the target retains references; removing the referrer removes them.
    [Test]
    public async Task TargetDeletionRetainsReferencesAndReferrerDeletionCascades()
    {
        await using var database = await DatabaseFixture.CreateAsync();
        await using var factory = new HeartbeatApiFactory(database.ConnectionString);
        using var http = factory.CreateClient();
        var schema = Schema(); var target = Business(schema);
        var inputs = new VersionSubmission(EntityId.New(), VersionId.New(), null, null, new InputSet([target.VersionId]));
        var next = inputs with { VersionId = VersionId.New(), PreviousVersionId = inputs.VersionId };
        using var saved = await Submit(http, schema, target, inputs, next);
        await Assert.That(saved.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var deleted = await http.DeleteAsync($"/entities/{target.EntityId.Value}");
        await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That((await Page(http, target.EntityId)).GetProperty("referrers").GetArrayLength()).IsEqualTo(1);
        await Assert.That((await Page(http, target.EntityId, "?versions=all")).GetProperty("referrers").GetArrayLength()).IsEqualTo(2);
        using var retry = await Submit(http, next);
        await Assert.That(retry.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var removed = await http.DeleteAsync($"/entities/{inputs.EntityId.Value}");
        await Assert.That(removed.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That((await Page(http, target.EntityId, "?versions=all")).GetProperty("referrers").GetArrayLength()).IsEqualTo(0);
    }

    // VERSION-13: a rejected suffix must leave only the accepted prefix's references.
    [Test]
    public async Task PartialSubmissionAndRetryPreserveExactlyTheSavedReferences()
    {
        await using var database = await DatabaseFixture.CreateAsync();
        await using var factory = new HeartbeatApiFactory(database.ConnectionString);
        using var http = factory.CreateClient();
        var schema = Schema(); var target = Business(schema);
        var first = new VersionSubmission(EntityId.New(), VersionId.New(), null, null, new InputSet([target.VersionId]));
        var rejected = new VersionSubmission(EntityId.New(), VersionId.New(), null, null, new InputSet([target.VersionId, VersionId.New()]));
        var later = first with { EntityId = EntityId.New(), VersionId = VersionId.New() };
        using var saved = await Submit(http, schema, target, first, rejected, later);
        await Assert.That((await Json(saved)).GetProperty("index").GetInt32()).IsEqualTo(3);
        var entry = (await Page(http, target.EntityId, "?versions=all")).GetProperty("referrers").EnumerateArray().Single();
        await Assert.That(Id(entry)).IsEqualTo(first.VersionId.Value);
        using var retry = await Submit(http, schema, target, first);
        await Assert.That(retry.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await Page(http, target.EntityId)).GetProperty("referrers").GetArrayLength()).IsEqualTo(1);
    }

    [Test]
    public async Task UnknownTargetsAndInvalidParametersFollowContract()
    {
        await using var database = await DatabaseFixture.CreateAsync();
        await using var factory = new HeartbeatApiFactory(database.ConnectionString);
        using var http = factory.CreateClient();
        var schema = Schema();
        using var saved = await Submit(http, schema);
        await Assert.That(saved.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var missing = await http.GetAsync($"/entities/{EntityId.New().Value}/referrers");
        await Assert.That(missing.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        var wrongKind = Uri.EscapeDataString(Convert.ToBase64String(Encoding.UTF8.GetBytes("entities|" + schema.EntityId.Value)));
        foreach (var query in new[] { "versions=old", "after=" + wrongKind })
        {
            using var bad = await http.GetAsync($"/entities/{schema.EntityId.Value}/referrers?{query}");
            await Assert.That(bad.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        }
        await Assert.That((await Page(http, schema.EntityId, "?limit=1000")).GetProperty("referrers").GetArrayLength()).IsEqualTo(0);
    }

    private static Guid Id(JsonElement referrer) => referrer.GetProperty("version").GetProperty("versionId").GetGuid();
    private static async Task<JsonElement> Page(HttpClient http, EntityId target, string query = "")
    {
        using var response = await http.GetAsync($"/entities/{target.Value}/referrers{query}");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        return await Json(response);
    }
    private static async Task Locations(JsonElement entry, params (string Path, VersionId Target)[] expected)
    {
        var actual = entry.GetProperty("references").EnumerateArray()
            .Select(r => (r.GetProperty("path").GetString()!, new VersionId(r.GetProperty("targetVersionId").GetGuid()))).ToArray();
        await Assert.That(actual.Length).IsEqualTo(expected.Length);
        await Assert.That(actual.ToHashSet().SetEquals(expected)).IsTrue();
    }
}
