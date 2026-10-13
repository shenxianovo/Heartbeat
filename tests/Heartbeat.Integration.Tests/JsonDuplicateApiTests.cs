using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Heartbeat.Core;
using Heartbeat.Testing;

namespace Heartbeat.Integration.Tests;

public sealed class JsonDuplicateApiTests
{
    private TestDatabase? _database;
    [Before(Test)]
    public async Task CreateDatabaseAsync(CancellationToken cancellationToken) =>
        _database = await PostgresAssemblyHooks.Instance.CreateDatabaseAsync(cancellationToken);
    [After(Test)]
    public async Task DeleteDatabaseAsync() { if (_database is not null) await _database.DisposeAsync(); }

    [Test]
    [Arguments("{\"samples\":[{\"value\":\"first\",\"value\":\"second\"}]}")]
    [Arguments("{\"samples\":[{\"value\":\"first\",\"val\\u0075e\":\"second\"}]}")]
    [Arguments("{\"samples\":[{\"value\":\"valid\"},{\"value\":\"first\",\"value\":\"second\"}]}")]
    [Arguments("{\"samples\":[{\"value\":\"first\"}],\"samples\":[{\"value\":\"second\"}]}")]
    public async Task AmbiguousNestedContentDoesNotReplaceExistingEvidence(string properties)
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var factory = new HeartbeatApiFactory(_database!.ConnectionString);
        using var client = factory.CreateClient();
        var schemaId = EntityId.New().Value;
        var id = EntityId.New().Value;
        using var fields = JsonDocument.Parse("""{"samples":{"type":"array","required":true,"nullable":false,"items":{"type":"object","nullable":false,"fields":{"value":{"type":"string","required":true,"nullable":false}}}}}""");
        using var registered = await client.PutAsJsonAsync($"entities/schemas/{schemaId}",
            new { references = new { }, properties = new { name = "Samples", resourceName = "duplicate-samples", fields = fields.RootElement } }, ct);
        await Assert.That(registered.StatusCode).IsEqualTo(HttpStatusCode.Created);
        using var saved = await client.PutAsJsonAsync($"entities/duplicate-samples/{id}",
            new { references = new { }, properties = new { samples = new[] { new { value = "original" } } } }, ct);
        await Assert.That(saved.StatusCode).IsEqualTo(HttpStatusCode.Created);
        using var body = new StringContent("{\"references\":{},\"properties\":" + properties + "}", Encoding.UTF8, "application/json");
        using var rejected = await client.PutAsync($"entities/duplicate-samples/{id}", body, ct);
        await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        var read = await client.GetFromJsonAsync<JsonElement>($"entities/{id}", ct);
        await Assert.That(read.GetProperty("properties").GetProperty("samples")[0].GetProperty("value").GetString()).IsEqualTo("original");
    }
}
