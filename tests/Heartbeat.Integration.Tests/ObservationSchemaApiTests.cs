using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Heartbeat.Core;
using Heartbeat.Testing;

namespace Heartbeat.Integration.Tests;

public sealed class ObservationSchemaApiTests
{
    private TestDatabase? _database;

    [Before(Test)]
    public async Task CreateDatabaseAsync(CancellationToken cancellationToken)
    {
        _database = await PostgresAssemblyHooks.Instance.CreateDatabaseAsync(cancellationToken);
    }

    [After(Test)]
    public async Task DeleteDatabaseAsync()
    {
        if (_database is not null)
        {
            await _database.DisposeAsync();
            _database = null;
        }
    }

    [Test]
    public async Task SchemaCanBeCreatedRepeatedAndUpdatedWithoutSemanticComparison()
    {
        var cancellationToken = TestContext.Current!.Execution.CancellationToken;
        var id = EntityId.New().Value;
        await using var factory = new HeartbeatApiFactory(_database!.ConnectionString);
        using var client = factory.CreateClient();
        var original = new
        {
            name = "Window title",
            schema = new { description = "Title observed on a window" },
        };
        using var created = await client.PutAsJsonAsync(
            $"/entities/observation-schemas/{id}", original, cancellationToken);
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(await created.Content.ReadAsStringAsync(cancellationToken)).IsEqualTo("");
        using var repeated = await client.PutAsJsonAsync(
            $"/entities/observation-schemas/{id}", original, cancellationToken);
        await Assert.That(repeated.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        using var updated = await client.PutAsJsonAsync($"/entities/observation-schemas/{id}", new
        {
            name = "Window caption",
            schema = new { description = "The observed window title" },
        }, cancellationToken);
        await Assert.That(updated.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(await updated.Content.ReadAsStringAsync(cancellationToken)).IsEqualTo("");

        using var read = await client.GetAsync($"/entities/{id}", cancellationToken);
        await Assert.That(read.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var body = await read.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        await Assert.That(body.GetProperty("category").GetString()).IsEqualTo("observation_schema");
        var entity = body.GetProperty("entity");
        await Assert.That(entity.GetProperty("name").GetString()).IsEqualTo("Window caption");
        await Assert.That(entity.GetProperty("schema").GetProperty("description").GetString())
            .IsEqualTo("The observed window title");
        await Assert.That(entity.EnumerateObject().Count()).IsEqualTo(2);
    }

    [Test]
    [Arguments("null")]
    [Arguments("true")]
    [Arguments("42")]
    [Arguments("\"Explanation\"")]
    [Arguments("[{\"field\":\"title\"}]")]
    public async Task SchemaAcceptsAnyRepresentableJsonValue(string json)
    {
        var cancellationToken = TestContext.Current!.Execution.CancellationToken;
        var id = EntityId.New().Value;
        var schema = JsonSerializer.Deserialize<JsonElement>(json);
        await using var factory = new HeartbeatApiFactory(_database!.ConnectionString);
        using var client = factory.CreateClient();
        using var created = await client.PutAsJsonAsync($"/entities/observation-schemas/{id}", new
        {
            name = "Definition",
            schema,
        }, cancellationToken);
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        using var read = await client.GetAsync($"/entities/{id}", cancellationToken);
        await Assert.That(read.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var body = await read.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var entity = body.GetProperty("entity");
        await Assert.That(JsonElement.DeepEquals(entity.GetProperty("schema"), schema)).IsTrue();
    }

    [Test]
    public async Task SchemaCategoryConflictPreservesTheExistingObserver()
    {
        var cancellationToken = TestContext.Current!.Execution.CancellationToken;
        var id = EntityId.New().Value;
        await using var factory = new HeartbeatApiFactory(_database!.ConnectionString);
        using var client = factory.CreateClient();
        using var created = await client.PutAsJsonAsync(
            $"/entities/observers/{id}", new { name = "Original observer" }, cancellationToken);
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        using var rejected = await client.PutAsJsonAsync(
            $"/entities/observation-schemas/{id}", NewSchema(), cancellationToken);
        await AssertProblemAsync(rejected, HttpStatusCode.Conflict, cancellationToken);
        using var read = await client.GetAsync($"/entities/{id}", cancellationToken);
        await Assert.That(read.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var body = await read.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        await Assert.That(body.GetProperty("category").GetString()).IsEqualTo("observer");
        await Assert.That(body.GetProperty("entity").GetProperty("name").GetString())
            .IsEqualTo("Original observer");
    }

    [Test]
    [Arguments("missingSchema")]
    [Arguments("nullName")]
    [Arguments("unrepresentableSchema")]
    [Arguments("bodyIdentity")]
    public async Task InvalidSchemaReplacementPreservesTheSavedContent(string invalidField)
    {
        var cancellationToken = TestContext.Current!.Execution.CancellationToken;
        var id = EntityId.New().Value;
        await using var factory = new HeartbeatApiFactory(_database!.ConnectionString);
        using var client = factory.CreateClient();
        var request = NewSchema();
        using var created = await client.PutAsJsonAsync(
            $"/entities/observation-schemas/{id}", request, cancellationToken);
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var invalid = (JsonObject)request.DeepClone();
        invalid["name"] = "Replacement";
        switch (invalidField)
        {
            case "missingSchema":
                invalid.Remove("schema");
                break;
            case "nullName":
                invalid["name"] = null;
                break;
            case "unrepresentableSchema":
                invalid["schema"] = "\0";
                break;
            case "bodyIdentity":
                invalid["id"] = id;
                break;
        }

        using var rejected = await client.PutAsJsonAsync(
            $"/entities/observation-schemas/{id}", invalid, cancellationToken);
        await AssertProblemAsync(rejected, HttpStatusCode.BadRequest, cancellationToken);
        using var read = await client.GetAsync($"/entities/{id}", cancellationToken);
        await Assert.That(read.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var body = await read.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var entity = body.GetProperty("entity");
        await Assert.That(entity.GetProperty("name").GetString()).IsEqualTo("Original definition");
        await Assert.That(entity.GetProperty("schema").ValueKind).IsEqualTo(JsonValueKind.Null);
    }

    private static JsonObject NewSchema() => new()
    {
        ["name"] = "Original definition",
        ["schema"] = null,
    };

    private static async Task AssertProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode expectedStatus,
        CancellationToken cancellationToken)
    {
        await Assert.That(response.StatusCode).IsEqualTo(expectedStatus);
        await Assert.That(response.Content.Headers.ContentType?.MediaType)
            .IsEqualTo("application/problem+json");
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        await Assert.That(problem.GetProperty("type").GetString()).IsEqualTo("about:blank");
        await Assert.That(problem.GetProperty("status").GetInt32()).IsEqualTo((int)expectedStatus);
        await Assert.That(string.IsNullOrWhiteSpace(problem.GetProperty("detail").GetString())).IsFalse();
    }
}
