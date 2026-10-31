using System.Text.Json;
using Npgsql;
using Orisumi.Core;
using Orisumi.Infrastructure.Database;
using Orisumi.Testing;

namespace Orisumi.Integration.Tests;

public sealed class ProjectionTests
{
    [Test, Property("caseId", "storage-projections")]
    public async Task RebuildPreservesHistoryDeletedTargetsAndAllQueryResults()
    {
        await using var database = await DatabaseFixture.CreateAsync();
        await using var factory = new OrisumiApiFactory(database.ConnectionString);
        using var http = factory.CreateClient();
        var writer = new WriteClient(http); await writer.InitializeAsync();
        var schema = await writer.DeclareAsync();
        var content = await writer.WriteAsync(schema, new { }, "content", time: new { startAt = "1970-01-01T00:00:00Z", endAt = (string?)null, timeZone = (string?)null });
        var result = await writer.SendAsync([WriteClient.Write(schema, new { }, "content", time: new { startAt = (string?)null, endAt = "1970-01-01T01:00:00Z", timeZone = (string?)null })]);
        var observation = VersionJson.ReadSaved(result.GetProperty("results")[0].GetProperty("observation"));
        using var deleted = await http.DeleteAsync($"/entities/{content.EntityId.Value}"); deleted.EnsureSuccessStatusCode();
        var paths = new[] { "entities", "entities?resourceName=items", $"entities/{observation.EntityId.Value}/versions",
            $"entities/{content.EntityId.Value}/referrers?versions=all", "observations?from=1970-01-01T00:00:00Z&to=1970-01-02T00:00:00Z",
            $"observations?contentEntityId={content.EntityId.Value}&observerEntityId={writer.Observer.EntityId.Value}",
            "observations?resourceName=items" };
        var before = new List<JsonElement>();
        foreach (var path in paths) { using var response = await http.GetAsync(path); response.EnsureSuccessStatusCode(); before.Add(await WriteClient.JsonAsync(response)); }
        await Assert.That(before[5].GetProperty("versions").GetArrayLength()).IsEqualTo(1);
        await Assert.That(before[6].GetProperty("versions").GetArrayLength()).IsEqualTo(0);
        await using var source = NpgsqlDataSource.Create(database.ConnectionString);
        await new PostgresEntityStore(source).RebuildProjectionsAsync();
        for (var i = 0; i < paths.Length; i++)
        {
            using var response = await http.GetAsync(paths[i]); response.EnsureSuccessStatusCode();
            await Assert.That(JsonElement.DeepEquals(before[i], await WriteClient.JsonAsync(response))).IsTrue();
        }
        // Recognition and stream state are authoritative, and survive rebuilding projections.
        var unchanged = await writer.WriteAsync(new(Guid.Parse("019e1bbb-ec00-7000-8000-00000000ffff")), new { }, entityId: writer.Observer.EntityId);
        await Assert.That(unchanged.VersionId).IsEqualTo(writer.Observer.VersionId);
    }
}
