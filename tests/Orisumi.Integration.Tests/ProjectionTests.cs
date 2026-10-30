using System.Net;
using System.Text.Json;
using Npgsql;
using Orisumi.Infrastructure.Database;
using Orisumi.Core;

namespace Orisumi.Integration.Tests;

public sealed class ProjectionTests
{
    [Test]
    [Property("caseId", "storage-projections")]
    public async Task DeletingContentRemovesItsTypeFromTimeQueriesButKeepsObservationAndReferences()
    {
        await using var database = await DatabaseFixture.CreateAsync();
        await using var factory = new OrisumiApiFactory(database.ConnectionString);
        using var http = factory.CreateClient();
        var schema = EntityApiTests.Schema();
        var observer = EntityApiTests.Business(schema);
        var content = EntityApiTests.Business(schema);
        var observation = new VersionSubmission(EntityId.New(), VersionId.New(), null, null,
            new Observation(observer.VersionId, content.VersionId, null,
                DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddHours(1), null));
        using var saved = await EntityApiTests.Submit(http, schema, observer, content, observation);
        await Assert.That(saved.StatusCode).IsEqualTo(HttpStatusCode.OK);
        const string query = "/observations?from=1970-01-01T00:00:00Z&to=1970-01-02T00:00:00Z";
        using var before = await http.GetAsync(query + "&resourceName=items");
        await Assert.That((await EntityApiTests.Json(before)).GetProperty("versions").GetArrayLength()).IsEqualTo(1);
        using var deleted = await http.DeleteAsync($"/entities/{content.EntityId.Value}");
        await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        using var typed = await http.GetAsync(query + "&resourceName=items");
        await Assert.That((await EntityApiTests.Json(typed)).GetProperty("versions").GetArrayLength()).IsEqualTo(0);
        using var untyped = await http.GetAsync(query);
        await Assert.That((await EntityApiTests.Json(untyped)).GetProperty("versions").GetArrayLength()).IsEqualTo(1);
        using var refs = await http.GetAsync($"/entities/{content.EntityId.Value}/referrers");
        var reference = (await EntityApiTests.Json(refs)).GetProperty("referrers")[0].GetProperty("references")[0];
        await Assert.That(reference.GetProperty("path").GetString()).IsEqualTo("/content/contentVersionId");
        await Assert.That(reference.GetProperty("targetVersionId").GetGuid()).IsEqualTo(content.VersionId.Value);
    }
    [Test, Property("caseId", "storage-projections")]
    public async Task RebuildPreservesHistoryDeletedTargetsAndAllQueryResults()
    {
        await using var database = await DatabaseFixture.CreateAsync();
        await using var factory = new OrisumiApiFactory(database.ConnectionString);
        using var http = factory.CreateClient();
        var schema = EntityApiTests.Schema();
        var observer = EntityApiTests.Business(schema);
        var content = EntityApiTests.Business(schema);
        var first = new VersionSubmission(EntityId.New(), VersionId.New(), null, null,
            new Observation(observer.VersionId, content.VersionId, null, DateTimeOffset.UnixEpoch, null, null));
        var current = first with { VersionId = VersionId.New(), PreviousVersionId = first.VersionId,
            Content = new Observation(observer.VersionId, content.VersionId, null, null, DateTimeOffset.UnixEpoch.AddHours(1), null) };
        using var saved = await EntityApiTests.Submit(http, schema, observer, content, first, current);
        saved.EnsureSuccessStatusCode();
        using var deleted = await http.DeleteAsync($"entities/{content.EntityId.Value}");
        deleted.EnsureSuccessStatusCode();
        var paths = new[] { "entities", "entities?resourceName=items", $"entities/{first.EntityId.Value}/versions",
            $"entities/{content.EntityId.Value}/referrers?versions=all", "observations?from=1970-01-01T00:00:00Z&to=1970-01-02T00:00:00Z" };
        var before = new List<JsonElement>();
        foreach (var path in paths) { using var response = await http.GetAsync(path); response.EnsureSuccessStatusCode(); before.Add(await EntityApiTests.Json(response)); }
        await using var source = NpgsqlDataSource.Create(database.ConnectionString);
        await new PostgresEntityStore(source).RebuildProjectionsAsync();
        for (var i = 0; i < paths.Length; i++)
        {
            using var response = await http.GetAsync(paths[i]); response.EnsureSuccessStatusCode();
            await Assert.That(JsonElement.DeepEquals(before[i], await EntityApiTests.Json(response))).IsTrue();
        }
    }

}
