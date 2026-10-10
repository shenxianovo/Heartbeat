using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Heartbeat.Core;
using Heartbeat.Testing;
using Npgsql;

namespace Heartbeat.Integration.Tests;

public sealed class EntitySchemaApiTests
{
    private TestDatabase? _database;
    [Before(Test)]
    public async Task CreateDatabaseAsync(CancellationToken cancellationToken) =>
        _database = await PostgresAssemblyHooks.Instance.CreateDatabaseAsync(cancellationToken);
    [After(Test)]
    public async Task DeleteDatabaseAsync() { if (_database is not null) await _database.DisposeAsync(); }

    [Test]
    public async Task SchemaRegistrationIsIdempotentAndOnlyDisplayNameCanChange()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var factory = new HeartbeatApiFactory(_database!.ConnectionString);
        using var client = factory.CreateClient();
        var id = await EntityTestRequests.RegisterAsync(client, "test-records", ct);
        await EntityTestRequests.RegisterAsync(client, "test-records", ct, schemaId: id);
        using var renamed = await client.PutAsJsonAsync($"entities/schemas/{id}",
            EntityTestRequests.Business(new { name = "Renamed", resourceName = "test-records", fields = EntityTestRequests.Fields }), ct);
        await Assert.That(renamed.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        var read = await client.GetFromJsonAsync<JsonElement>($"entities/{id}", ct);
        await Assert.That(read.GetProperty("category").GetString()).IsEqualTo("entity_schema");
        await Assert.That(read.GetProperty("properties").GetProperty("name").GetString()).IsEqualTo("Renamed");
        foreach (var request in new[]
        {
            EntityTestRequests.Business(new { name = "Changed", resourceName = "renamed-resource", fields = EntityTestRequests.Fields }),
            EntityTestRequests.Business(new { name = "Changed", resourceName = "test-records", fields = JsonSerializer.Deserialize<JsonElement>("{}") }),
        })
        {
            using var rejected = await client.PutAsJsonAsync($"entities/schemas/{id}", request, ct);
            await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        }
    }

    [Test]
    [Arguments("schemas")]
    [Arguments("observers")]
    [Arguments("observations")]
    [Arguments("Uppercase")]
    [Arguments("a/b")]
    [Arguments("a\n")]
    public async Task InvalidResourceNameDoesNotRegister(string resourceName)
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var factory = new HeartbeatApiFactory(_database!.ConnectionString);
        using var client = factory.CreateClient();
        var id = EntityId.New().Value;
        using var rejected = await client.PutAsJsonAsync($"entities/schemas/{id}",
            EntityTestRequests.Business(new { name = "Test", resourceName, fields = EntityTestRequests.Fields }), ct);
        await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        using var absent = await client.GetAsync($"entities/{id}", ct);
        await Assert.That(absent.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    [Arguments("{\"field\":{\"type\":\"string\",\"nullable\":false}}")]
    [Arguments("{\"field\":{\"type\":\"string\",\"required\":false}}")]
    [Arguments("{\"field\":{\"required\":false,\"nullable\":false}}")]
    [Arguments("{\"field\":{\"type\":\"object\",\"required\":false,\"nullable\":false}}")]
    [Arguments("{\"field\":{\"type\":\"array\",\"required\":false,\"nullable\":false,\"items\":{\"type\":\"string\",\"nullable\":false,\"required\":true}}}")]
    [Arguments("{\"field\":{\"type\":\"array\",\"required\":false,\"nullable\":false,\"items\":{\"type\":\"reference\",\"nullable\":false}}}")]
    [Arguments("{\"field\":{\"type\":\"object\",\"required\":false,\"nullable\":false,\"fields\":{\"r\":{\"type\":\"reference\",\"required\":true,\"nullable\":false}}}}")]
    [Arguments("{\"id\":{\"type\":\"string\",\"required\":false,\"nullable\":false}}")]
    [Arguments("{\"__omitted_fields\":{\"type\":\"string\",\"required\":false,\"nullable\":false}}")]
    [Arguments("{\"aB\":{\"type\":\"string\",\"required\":false,\"nullable\":false},\"a_b\":{\"type\":\"string\",\"required\":false,\"nullable\":false}}")]
    [Arguments("{\"field\":{\"type\":\"string\",\"required\":false,\"nullable\":false,\"fields\":{}}}")]
    [Arguments("{\"field\":{\"type\":\"string\",\"required\":false,\"nullable\":false,\"description\":\"unknown\"}}")]
    public async Task InvalidDeclarationsAreRejectedAtomically(string fields)
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var factory = new HeartbeatApiFactory(_database!.ConnectionString);
        using var client = factory.CreateClient();
        var id = EntityId.New().Value;
        using var rejected = await client.PutAsJsonAsync($"entities/schemas/{id}",
            EntityTestRequests.Business(new { name = "Test", resourceName = "test-records", fields = JsonSerializer.Deserialize<JsonElement>(fields) }), ct);
        await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        using var absent = await client.GetAsync($"entities/{id}", ct);
        await Assert.That(absent.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task ConcurrentRegistrationsDoNotLeaveLosingSchemaIndexesOrTables()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var factory = new HeartbeatApiFactory(_database!.ConnectionString);
        using var client = factory.CreateClient();
        var ids = Enumerable.Range(0, 8).Select(_ => EntityId.New().Value).ToArray();
        var responses = await Task.WhenAll(ids.Select(id => client.PutAsJsonAsync($"entities/schemas/{id}",
            EntityTestRequests.Business(new { name = "Test", resourceName = "test-records", fields = EntityTestRequests.Fields }), ct)));
        try
        {
            await Assert.That(responses.Count(r => r.StatusCode == HttpStatusCode.Created)).IsEqualTo(1);
            await Assert.That(responses.Count(r => r.StatusCode == HttpStatusCode.Conflict)).IsEqualTo(7);
            for (var i = 0; i < ids.Length; i++)
            {
                using var read = await client.GetAsync($"entities/{ids[i]}", ct);
                await Assert.That(read.StatusCode).IsEqualTo(responses[i].StatusCode == HttpStatusCode.Created ? HttpStatusCode.OK : HttpStatusCode.NotFound);
            }
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    [Test]
    public async Task ConcurrentSameIdentityRegistersOnceAndDefinitionKeyOrderIsIgnored()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var factory = new HeartbeatApiFactory(_database!.ConnectionString);
        using var client = factory.CreateClient();
        var id = EntityId.New().Value;
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => client.PutAsJsonAsync($"entities/schemas/{id}",
            EntityTestRequests.Business(new { name = "Test", resourceName = "test-records", fields = EntityTestRequests.Fields }), ct)));
        try
        {
            await Assert.That(responses.Count(r => r.StatusCode == HttpStatusCode.Created)).IsEqualTo(1);
            await Assert.That(responses.Count(r => r.StatusCode == HttpStatusCode.NoContent)).IsEqualTo(7);
        }
        finally { foreach (var response in responses) response.Dispose(); }
        static JsonNode Reverse(JsonNode node) => node switch
        {
            JsonObject obj => new JsonObject(obj.Reverse().Select(p => new KeyValuePair<string, JsonNode?>(p.Key, p.Value is null ? null : Reverse(p.Value)))),
            JsonArray array => new JsonArray(array.Select(p => p is null ? null : Reverse(p)).ToArray()),
            _ => node.DeepClone(),
        };
        var fields = Reverse(JsonNode.Parse(EntityTestRequests.Fields.GetRawText())!);
        using var reordered = await client.PutAsJsonAsync($"entities/schemas/{id}",
            EntityTestRequests.Business(new { name = "Test", resourceName = "test-records", fields }), ct);
        await Assert.That(reordered.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
    }

    [Test]
    public async Task UnrepresentableSchemaReplacementReturnsBadRequestAndKeepsOriginal()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var factory = new HeartbeatApiFactory(_database!.ConnectionString);
        using var client = factory.CreateClient();
        var id = await EntityTestRequests.RegisterAsync(client, "test-records", ct);
        var fields = JsonSerializer.Deserialize<JsonElement>("""
            {"metadata":{"type":"object","required":false,"nullable":true,"fields":{"\u0000":{"type":"string","required":false,"nullable":true}}}}
            """);
        using var rejected = await client.PutAsJsonAsync($"entities/schemas/{id}",
            EntityTestRequests.Business(new { name = "Changed", resourceName = "test-records", fields }), ct);
        await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        var original = await client.GetFromJsonAsync<JsonElement>($"entities/{id}", ct);
        await Assert.That(original.GetProperty("properties").GetProperty("name").GetString()).IsEqualTo("Test records");
        await Assert.That(JsonElement.DeepEquals(original.GetProperty("properties").GetProperty("fields"), EntityTestRequests.Fields)).IsTrue();
    }

    [Test]
    public async Task PostgreSqlSystemColumnNameIsRejectedWithoutRegisteringSchemaOrResource()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var factory = new HeartbeatApiFactory(_database!.ConnectionString);
        using var client = factory.CreateClient();
        var id = EntityId.New().Value;
        var fields = JsonSerializer.Deserialize<JsonElement>("""
            {"ctid":{"type":"string","required":false,"nullable":true}}
            """);
        using var rejected = await client.PutAsJsonAsync($"entities/schemas/{id}",
            EntityTestRequests.Business(new { name = "Invalid", resourceName = "test-records", fields }), ct);
        await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        using var absent = await client.GetAsync($"entities/{id}", ct);
        await Assert.That(absent.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        using var unregistered = await client.PutAsJsonAsync($"entities/test-records/{EntityId.New().Value}",
            EntityTestRequests.Business(new { }), ct);
        await Assert.That(unregistered.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    [Property("caseId", "entity-schema-registration-table-conflict")]
    public async Task ExistingPhysicalTableMakesRegistrationRollBack()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var connection = new NpgsqlConnection(_database!.ConnectionString + ";Pooling=false");
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand("CREATE TABLE entity_test_records (original text)", connection);
        await command.ExecuteNonQueryAsync(ct);
        await connection.CloseAsync();
        await using var factory = new HeartbeatApiFactory(_database.ConnectionString);
        using var client = factory.CreateClient();
        var id = EntityId.New().Value;
        using var rejected = await client.PutAsJsonAsync($"entities/schemas/{id}",
            EntityTestRequests.Business(new { name = "Test", resourceName = "test-records", fields = new { title = new { type = "string", required = false, nullable = true } } }), ct);
        await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        using var absent = await client.GetAsync($"entities/{id}", ct);
        await Assert.That(absent.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        using var business = await client.PutAsJsonAsync($"entities/test-records/{EntityId.New().Value}", EntityTestRequests.Business(new { }), ct);
        await Assert.That(business.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }
}
