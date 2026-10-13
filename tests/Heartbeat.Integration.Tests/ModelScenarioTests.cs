using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Heartbeat.Core;
using Heartbeat.Testing;

namespace Heartbeat.Integration.Tests;

public sealed class ModelScenarioTests
{
    private TestDatabase? _database;
    [Before(Test)]
    public async Task CreateDatabaseAsync(CancellationToken cancellationToken) =>
        _database = await PostgresAssemblyHooks.Instance.CreateDatabaseAsync(cancellationToken);
    [After(Test)]
    public async Task DeleteDatabaseAsync() { if (_database is not null) await _database.DisposeAsync(); }

    public static IEnumerable<(string Id, string Json)> Cases()
    {
        foreach (var path in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures"), "*.json").Order(StringComparer.Ordinal))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var item in document.RootElement.EnumerateArray())
                if (item.GetProperty("expectedValid").GetBoolean())
                    yield return (item.GetProperty("id").GetString()!, item.GetRawText());
        }
    }

    [Test]
    [MethodDataSource(nameof(Cases))]
    public async Task ScenarioContentRoundTripsAndReplayKeepsIdentity(string scenarioId, string json)
    {
        var ct = TestContext.Current!.Execution.CancellationToken;
        using var document = JsonDocument.Parse(json);
        var item = document.RootElement;
        await using var factory = new HeartbeatApiFactory(_database!.ConnectionString);
        using var client = factory.CreateClient();
        var schemaId = EntityId.New().Value;
        var contentId = EntityId.New().Value;
        var observerId = EntityId.New().Value;
        var observationId = EntityId.New().Value;
        var resource = "stress-" + scenarioId.ToLowerInvariant();
        using var schema = await client.PutAsJsonAsync($"entities/schemas/{schemaId}",
            new { references = new { }, properties = new { name = scenarioId, resourceName = resource, fields = item.GetProperty("fields") } }, ct);
        await Assert.That(schema.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var content = new { references = item.GetProperty("references"), properties = item.GetProperty("properties") };
        using var saved = await client.PutAsJsonAsync($"entities/{resource}/{contentId}", content, ct);
        await Assert.That(saved.StatusCode).IsEqualTo(HttpStatusCode.Created);
        using var replay = await client.PutAsJsonAsync($"entities/{resource}/{contentId}", content, ct);
        await Assert.That(replay.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        var read = await client.GetFromJsonAsync<JsonElement>($"entities/{contentId}", ct);
        await Assert.That(read.GetProperty("schemaId").GetGuid()).IsEqualTo(schemaId);
        var definition = await client.GetFromJsonAsync<JsonElement>($"entities/{read.GetProperty("schemaId").GetGuid()}", ct);
        await Assert.That(definition.GetProperty("category").GetString()).IsEqualTo("entity_schema");
        await Assert.That(definition.GetProperty("properties").GetProperty("resourceName").GetString()).IsEqualTo(resource);
        await Assert.That(JsonElement.DeepEquals(definition.GetProperty("properties").GetProperty("fields"), item.GetProperty("fields"))).IsTrue();
        await Assert.That(definition.TryGetProperty("schemaId", out _)).IsFalse();
        await Assert.That(JsonElement.DeepEquals(read.GetProperty("references"), content.references)).IsTrue();
        await Assert.That(JsonElement.DeepEquals(read.GetProperty("properties"), content.properties)).IsTrue();

        using var observer = await client.PutAsJsonAsync($"entities/observers/{observerId}",
            new { references = new { }, properties = new { name = "Scenario validation collector" } }, ct);
        await Assert.That(observer.StatusCode).IsEqualTo(HttpStatusCode.Created);
        // Only executable time envelopes supplied by the fixture are asserted. Narrative mappings
        // do not license invented instants, so those cases retain unknown boundaries.
        var properties = item.TryGetProperty("observation", out var observation) && observation.ValueKind == JsonValueKind.Object
            ? observation.GetProperty("properties")
            : JsonSerializer.SerializeToElement(new { startAt = (string?)null, endAt = (string?)null, timeZone = (string?)null });
        var observationContent = new { references = new { observerId, contentId, schemaId }, properties };
        using var savedObservation = await client.PutAsJsonAsync($"entities/observations/{observationId}", observationContent, ct);
        await Assert.That(savedObservation.StatusCode).IsEqualTo(HttpStatusCode.Created);
        using var replayObservation = await client.PutAsJsonAsync($"entities/observations/{observationId}", observationContent, ct);
        await Assert.That(replayObservation.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        var observed = await client.GetFromJsonAsync<JsonElement>($"entities/{observationId}", ct);
        await Assert.That(observed.GetProperty("references").GetProperty("contentId").GetGuid()).IsEqualTo(contentId);
        await Assert.That(observed.GetProperty("references").GetProperty("schemaId").GetGuid()).IsEqualTo(schemaId);
        await Assert.That(observed.TryGetProperty("schemaId", out _)).IsFalse();
        var observerRead = await client.GetFromJsonAsync<JsonElement>($"entities/{observerId}", ct);
        await Assert.That(observerRead.TryGetProperty("schemaId", out _)).IsFalse();
        var savedProperties = observed.GetProperty("properties");
        await Assert.That(savedProperties.GetProperty("timeZone").GetString()).IsEqualTo(properties.GetProperty("timeZone").GetString());
        foreach (var boundary in new[] { "startAt", "endAt" })
        {
            if (properties.GetProperty(boundary).ValueKind == JsonValueKind.Null)
                await Assert.That(savedProperties.GetProperty(boundary).ValueKind).IsEqualTo(JsonValueKind.Null);
            else
            {
                var expected = properties.GetProperty(boundary).GetDateTimeOffset().UtcTicks;
                await Assert.That(savedProperties.GetProperty(boundary).GetDateTimeOffset().UtcTicks).IsEqualTo(expected - expected % 10);
            }
        }
    }
}
