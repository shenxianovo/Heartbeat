using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using Orisumi.Core;
using Orisumi.Infrastructure.Database;
using Orisumi.Testing;

namespace Orisumi.Integration.Tests;

public sealed class ProjectionTests
{
    [Test, Property("caseId", "storage-projections")]
    public async Task RebuildPreservesThingsReviewsHistoryAndDeletedTargets()
    {
        await using var database = await DatabaseFixture.CreateAsync();
        await using var factory = new OrisumiApiFactory(database.ConnectionString);
        using var http = factory.CreateClient();
        var writer = new WriteClient(http); await writer.InitializeAsync();
        var schema = await writer.DeclareAsync(fields: WriteApiTests.NameFields);
        var first = await writer.WriteAsync(schema, new { name = "one" }, "key");
        var current = await writer.WriteAsync(schema, new { name = "two" }, "key");
        var review = await writer.WriteAsync(new(Guid.Parse("019e1bbb-ec00-7000-8000-00000000000e")),
            new { targetVersionId = first.VersionId.Value, verdict = "deny", text = (string?)null });
        using var deleted = await http.DeleteAsync($"/entities/{first.EntityId.Value}"); deleted.EnsureSuccessStatusCode();
        var paths = new[] { "entities", "things", "observations?standing=current", "observations?standing=all",
            $"entities/{first.EntityId.Value}/referrers?versions=all", $"entities/{review.EntityId.Value}/versions" };
        var before = new List<JsonElement>();
        foreach (var path in paths) before.Add(await http.GetFromJsonAsync<JsonElement>(path));
        await using var source = NpgsqlDataSource.Create(database.ConnectionString);
        await new PostgresEntityStore(source).RebuildProjectionsAsync();
        for (var i = 0; i < paths.Length; i++)
            await Assert.That(JsonElement.DeepEquals(before[i], await http.GetFromJsonAsync<JsonElement>(paths[i]))).IsTrue();
        var recreated = await writer.WriteAsync(schema, new { name = "two" }, "key");
        await Assert.That(recreated.EntityId == current.EntityId).IsFalse();
        await Assert.That(((Observation)recreated.Content).ObserverVersionId).IsEqualTo(writer.Observer.VersionId);
    }
}
