using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Heartbeat.Core;
using Heartbeat.Testing;
using Npgsql;

namespace Heartbeat.Integration.Tests;

public sealed class BusinessEntityApiTests
{
    private TestDatabase? _database;
    [Before(Test)]
    public async Task CreateDatabaseAsync(CancellationToken cancellationToken) =>
        _database = await PostgresAssemblyHooks.Instance.CreateDatabaseAsync(cancellationToken);
    [After(Test)]
    public async Task DeleteDatabaseAsync() { if (_database is not null) await _database.DisposeAsync(); }

    [Test]
    [Arguments("1.0")]
    [Arguments("1e0")]
    [Arguments("9223372036854775807.0")]
    [Arguments("-9223372036854775808e0")]
    public async Task MathematicalIntegersUseExactBigintValues(string number)
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var factory = new HeartbeatApiFactory(_database!.ConnectionString);
        using var client = factory.CreateClient();
        await EntityTestRequests.RegisterAsync(client, "test-records", ct);
        var id = EntityId.New().Value;
        using var content = new StringContent("{\"references\":{},\"properties\":{\"count\":" + number + "}}", Encoding.UTF8, "application/json");
        using var created = await client.PutAsync($"entities/test-records/{id}", content, ct);
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var read = await client.GetFromJsonAsync<JsonElement>($"entities/{id}", ct);
        var integer = read.GetProperty("properties").GetProperty("count").GetInt64();
        await Assert.That(integer).IsEqualTo(number.StartsWith('-') ? long.MinValue : number.StartsWith("922", StringComparison.Ordinal) ? long.MaxValue : 1L);
    }

    [Test]
    public async Task QuotedFieldNamesCannotBecomeSqlOrFormatTokens()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var factory = new HeartbeatApiFactory(_database!.ConnectionString);
        using var client = factory.CreateClient();
        const string field = "{0}\"; DROP TABLE observers; --";
        var fields = JsonSerializer.SerializeToElement(new Dictionary<string, object>
            { [field] = new { type = "string", required = true, nullable = false } });
        await EntityTestRequests.RegisterAsync(client, "test-records", ct, fields);
        var id = EntityId.New().Value;
        using var created = await client.PutAsJsonAsync($"entities/test-records/{id}",
            EntityTestRequests.Business(new Dictionary<string, string> { [field] = "Value" }), ct);
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var read = await client.GetFromJsonAsync<JsonElement>($"entities/{id}", ct);
        await Assert.That(read.GetProperty("properties").GetProperty(field).GetString()).IsEqualTo("Value");
        using var observer = await client.PutAsJsonAsync($"entities/observers/{EntityId.New().Value}", EntityTestRequests.Business(new { name = "Unaffected" }), ct);
        await Assert.That(observer.StatusCode).IsEqualTo(HttpStatusCode.Created);
    }

    [Test]
    public async Task TypedStorageKeepsExactNumbersReferencesAndOmissionAcrossReplacement()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var factory = new HeartbeatApiFactory(_database!.ConnectionString);
        using var client = factory.CreateClient();
        await EntityTestRequests.RegisterAsync(client, "test-records", ct);
        var id = EntityId.New().Value;
        var target = EntityId.New().Value;
        var body = JsonSerializer.Deserialize<JsonElement>($$$"""
            {"references":{"relatedId":"{{{target}}}"},"properties":{"title":"中文","count":9223372036854775807,"amount":123456789012345678901234567890.1234567890123456789,"enabled":true,"metadata":{"visible":true,"note":null},"labels":[null,"a"]}}
            """);
        using var created = await client.PutAsJsonAsync($"entities/test-records/{id}", body, ct);
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var read = await client.GetFromJsonAsync<JsonElement>($"entities/{id}", ct);
        await Assert.That(JsonElement.DeepEquals(read.GetProperty("properties"), body.GetProperty("properties"))).IsTrue();
        await Assert.That(read.GetProperty("references").GetProperty("relatedId").GetString()).IsEqualTo(target.ToString());
        var replacement = EntityTestRequests.Business(new { title = (string?)null }, new { relatedId = (string?)null });
        using var updated = await client.PutAsJsonAsync($"entities/test-records/{id}", replacement, ct);
        await Assert.That(updated.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        read = await client.GetFromJsonAsync<JsonElement>($"entities/{id}", ct);
        await Assert.That(read.GetProperty("properties").EnumerateObject().Count()).IsEqualTo(1);
        await Assert.That(read.GetProperty("properties").GetProperty("title").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(read.GetProperty("references").GetProperty("relatedId").ValueKind).IsEqualTo(JsonValueKind.Null);
        using var omitted = await client.PutAsJsonAsync($"entities/test-records/{id}", EntityTestRequests.Business(new { }), ct);
        await Assert.That(omitted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        read = await client.GetFromJsonAsync<JsonElement>($"entities/{id}", ct);
        await Assert.That(read.GetProperty("references").EnumerateObject().Any()).IsFalse();
        await Assert.That(read.GetProperty("properties").EnumerateObject().Any()).IsFalse();
        await using var connection = new NpgsqlConnection(_database.ConnectionString + ";Pooling=false");
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand("SELECT column_name, data_type FROM information_schema.columns WHERE table_name = 'entity_test_records'", connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var types = new Dictionary<string, string>();
        while (await reader.ReadAsync(ct)) types.Add(reader.GetString(0), reader.GetString(1));
        await Assert.That(types["count"]).IsEqualTo("bigint");
        await Assert.That(types["amount"]).IsEqualTo("numeric");
        await Assert.That(types["related_id"]).IsEqualTo("uuid");
        await Assert.That(types["metadata"]).IsEqualTo("jsonb");
        await Assert.That(types["enabled"]).IsEqualTo("boolean");
        await Assert.That(types["title"]).IsEqualTo("text");
        await Assert.That(types["__omitted_fields"]).IsEqualTo("ARRAY");
    }

    [Test]
    [Arguments("{\"references\":{},\"properties\":{\"extra\":1}}")]
    [Arguments("{\"references\":{\"title\":null},\"properties\":{}}")]
    [Arguments("{\"references\":{},\"properties\":{\"relatedId\":null}}")]
    [Arguments("{\"references\":{},\"properties\":{\"count\":9223372036854775808}}")]
    [Arguments("{\"references\":{},\"properties\":{\"count\":1.5}}")]
    [Arguments("{\"references\":{},\"properties\":{\"count\":1e-100}}")]
    [Arguments("{\"references\":{\"relatedId\":\" 0192b056-7300-7000-8000-000000000001\"},\"properties\":{}}")]
    [Arguments("{\"references\":{\"relatedId\":\"0192b056-7300-7000-8000-000000000001\\n\"},\"properties\":{}}")]
    [Arguments("{\"references\":{},\"properties\":{\"enabled\":null}}")]
    [Arguments("{\"references\":{},\"properties\":{\"metadata\":{\"visible\":true,\"extra\":null}}}")]
    [Arguments("{\"references\":{},\"properties\":{\"metadata\":{}}}")]
    [Arguments("{\"references\":{},\"properties\":{\"labels\":[true]}}")]
    [Arguments("{\"references\":{\"relatedId\":\"00000000-0000-4000-8000-000000000000\"},\"properties\":{}}")]
    [Arguments("{\"references\":{},\"properties\":{\"title\":\"\\u0000\"}}")]
    [Arguments("{\"references\":{},\"properties\":{\"title\":\"\\ud800\"}}")]
    [Arguments("{\"references\":{},\"properties\":{\"amount\":1e1000000}}")]
    [Arguments("{\"references\":{},\"properties\":{\"__omitted_fields\":[]}}")]
    public async Task InvalidReplacementPreservesOriginalAndInvalidCreateLeavesNoIndex(string json)
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var factory = new HeartbeatApiFactory(_database!.ConnectionString);
        using var client = factory.CreateClient();
        await EntityTestRequests.RegisterAsync(client, "test-records", ct);
        var id = EntityId.New().Value;
        using var created = await client.PutAsJsonAsync($"entities/test-records/{id}", EntityTestRequests.Business(new { title = "Original" }), ct);
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        foreach (var entityId in new[] { id, EntityId.New().Value })
        {
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var rejected = await client.PutAsync($"entities/test-records/{entityId}", content, ct);
            await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            using var read = await client.GetAsync($"entities/{entityId}", ct);
            await Assert.That(read.StatusCode).IsEqualTo(entityId == id ? HttpStatusCode.OK : HttpStatusCode.NotFound);
            if (entityId == id)
            {
                var result = await read.Content.ReadFromJsonAsync<JsonElement>(ct);
                await Assert.That(result.GetProperty("properties").GetProperty("title").GetString()).IsEqualTo("Original");
            }
        }
    }

    [Test]
    public async Task NestedObjectAndArrayMembersKeepMissingAndNullSeparate()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var factory = new HeartbeatApiFactory(_database!.ConnectionString);
        using var client = factory.CreateClient();
        var fields = JsonSerializer.Deserialize<JsonElement>("""
            {"details":{"type":"object","required":false,"nullable":true,"fields":{"note":{"type":"string","required":false,"nullable":true}}},
             "entries":{"type":"array","required":true,"nullable":false,"items":{"type":"object","nullable":true,"fields":{"note":{"type":"string","required":false,"nullable":true}}}}}
            """);
        await EntityTestRequests.RegisterAsync(client, "test-records", ct, fields);
        var id = EntityId.New().Value;
        foreach (var properties in new[] { "{\"entries\":[{},null,{\"note\":null}]}" , "{\"details\":null,\"entries\":[]}", "{\"details\":{},\"entries\":[]}" })
        {
            var json = JsonSerializer.Deserialize<JsonElement>(properties);
            using var saved = await client.PutAsJsonAsync($"entities/test-records/{id}", EntityTestRequests.Business(json), ct);
            saved.EnsureSuccessStatusCode();
            var read = await client.GetFromJsonAsync<JsonElement>($"entities/{id}", ct);
            await Assert.That(JsonElement.DeepEquals(read.GetProperty("properties"), json)).IsTrue();
        }
        foreach (var properties in new[] { "{}", "{\"entries\":[{\"unknown\":1}]}" })
        {
            using var rejected = await client.PutAsJsonAsync($"entities/test-records/{id}", EntityTestRequests.Business(JsonSerializer.Deserialize<JsonElement>(properties)), ct);
            await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        }
    }

    [Test]
    public async Task UnknownResourcesAndCrossSchemaIdsCannotCreateOrReplaceContent()
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        await using var factory = new HeartbeatApiFactory(_database!.ConnectionString);
        using var client = factory.CreateClient();
        var id = EntityId.New().Value;
        using var missing = await client.PutAsJsonAsync($"entities/test-records/{id}", EntityTestRequests.Business(new { }), ct);
        await Assert.That(missing.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await EntityTestRequests.RegisterAsync(client, "test-records", ct);
        await EntityTestRequests.RegisterAsync(client, "other-records", ct);
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => client.PutAsJsonAsync(
            $"entities/{(i % 2 == 0 ? "test-records" : "other-records")}/{id}", EntityTestRequests.Business(new { title = "Saved" }), ct)));
        try
        {
            await Assert.That(responses.Count(r => r.StatusCode == HttpStatusCode.Created)).IsEqualTo(1);
            await Assert.That(responses.Count(r => r.StatusCode == HttpStatusCode.NoContent)).IsEqualTo(3);
            await Assert.That(responses.Count(r => r.StatusCode == HttpStatusCode.Conflict)).IsEqualTo(4);
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }
}
