using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Heartbeat.Application.Entities;
using Heartbeat.Core;
using Heartbeat.Testing;

namespace Heartbeat.Integration.Tests;

public sealed class EntityApiTests
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
    public async Task ObservationCanReferenceMissingEntitiesAndNormalizeTime()
    {
        var cancellationToken = TestContext.Current!.Execution.CancellationToken;
        var id = EntityId.New().Value;
        var observerId = EntityId.New().Value;
        var contentId = EntityId.New().Value;
        var schemaId = EntityId.New().Value;
        await using var factory = new HeartbeatApiFactory(_database!.ConnectionString);
        using var client = factory.CreateClient();
        var request = new
        {
            references = new { observerId, contentId, schemaId },
            properties = new
            {
                startAt = "2026-10-06T09:00:00.123456+08:00",
                endAt = "2026-10-06T02:00:00Z",
                timeZone = "Asia/Shanghai",
            },
        };
        using var created = await client.PutAsJsonAsync(
            $"/entities/observations/{id}", request, cancellationToken);
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        using var repeated = await client.PutAsJsonAsync(
            $"/entities/observations/{id}", request, cancellationToken);
        await Assert.That(repeated.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

        using var read = await client.GetAsync($"/entities/{id}", cancellationToken);
        await Assert.That(read.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var body = await read.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        await Assert.That(body.GetProperty("category").GetString()).IsEqualTo("observation");
        var entity = body.GetProperty("properties");
        await Assert.That(body.GetProperty("references").GetProperty("observerId").GetString()).IsEqualTo(observerId.ToString());
        await Assert.That(body.GetProperty("references").GetProperty("contentId").GetString()).IsEqualTo(contentId.ToString());
        await Assert.That(body.GetProperty("references").GetProperty("schemaId").GetString()).IsEqualTo(schemaId.ToString());
        await Assert.That(entity.GetProperty("startAt").GetString()).IsEqualTo("2026-10-06T01:00:00.123456Z");
        await Assert.That(entity.GetProperty("endAt").GetString()).IsEqualTo("2026-10-06T02:00:00Z");
        await Assert.That(entity.GetProperty("timeZone").GetString()).IsEqualTo("Asia/Shanghai");

        var replacement = NewUnknownTimeObservation();
        using var replaced = await client.PutAsJsonAsync(
            $"/entities/observations/{id}", replacement, cancellationToken);
        await Assert.That(replaced.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        using var reread = await client.GetAsync($"/entities/{id}", cancellationToken);
        await Assert.That(reread.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var replacedBody = await reread.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var replacedEntity = replacedBody.GetProperty("properties");
        await Assert.That(replacedBody.GetProperty("references").GetProperty("observerId").GetString())
            .IsEqualTo(replacement["references"]!["observerId"]!.GetValue<string>());
        await Assert.That(replacedBody.GetProperty("references").GetProperty("contentId").GetString())
            .IsEqualTo(replacement["references"]!["contentId"]!.GetValue<string>());
        await Assert.That(replacedBody.GetProperty("references").GetProperty("schemaId").GetString())
            .IsEqualTo(replacement["references"]!["schemaId"]!.GetValue<string>());
        await Assert.That(replacedEntity.GetProperty("startAt").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(replacedEntity.GetProperty("endAt").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(replacedEntity.GetProperty("timeZone").ValueKind).IsEqualTo(JsonValueKind.Null);
    }

    [Test]
    public async Task GenericSaveEndpointIsUnavailableAndCannotCreateAnEntity()
    {
        var cancellationToken = TestContext.Current!.Execution.CancellationToken;
        var id = EntityId.New().Value;
        await using var factory = new HeartbeatApiFactory(_database!.ConnectionString);
        using var client = factory.CreateClient();
        using var saved = await client.PutAsJsonAsync($"/entities/{id}", EntityTestRequests.Business(new { name = "Example" }),
            cancellationToken);
        await Assert.That(saved.StatusCode).IsEqualTo(HttpStatusCode.MethodNotAllowed);
        await Assert.That(saved.Content.Headers.Allow.Contains("PUT")).IsFalse();
        using var read = await client.GetAsync($"/entities/{id}", cancellationToken);
        await AssertProblemAsync(read, HttpStatusCode.NotFound, cancellationToken);
    }

    [Test]
    [Arguments("missingStartAt")]
    [Arguments("nullReference")]
    [Arguments("wrongReferenceVersion")]
    [Arguments("timeWithoutOffset")]
    [Arguments("reversedTime")]
    [Arguments("invalidTimeZone")]
    [Arguments("bodyIdentity")]
    public async Task InvalidObservationReplacementPreservesTheSavedContent(string invalidField)
    {
        var cancellationToken = TestContext.Current!.Execution.CancellationToken;
        var id = EntityId.New().Value;
        var request = NewUnknownTimeObservation();
        await using var factory = new HeartbeatApiFactory(_database!.ConnectionString);
        using var client = factory.CreateClient();
        using var created = await client.PutAsJsonAsync(
            $"/entities/observations/{id}", request, cancellationToken);
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);

        var invalid = (JsonObject)request.DeepClone();
        switch (invalidField)
        {
            case "missingStartAt":
                ((JsonObject)invalid["properties"]!).Remove("startAt");
                break;
            case "nullReference":
                invalid["references"]!["observerId"] = null;
                break;
            case "wrongReferenceVersion":
                invalid["references"]!["contentId"] = "8dcc4596-2650-4a1e-a998-53a5e7dc626a";
                break;
            case "timeWithoutOffset":
                invalid["properties"]!["startAt"] = "2026-10-06T01:00:00";
                break;
            case "reversedTime":
                invalid["properties"]!["startAt"] = "2026-10-06T03:00:00Z";
                invalid["properties"]!["endAt"] = "2026-10-06T10:00:00+08:00";
                break;
            case "invalidTimeZone":
                invalid["properties"]!["timeZone"] = "Unknown/Place";
                break;
            case "bodyIdentity":
                invalid["id"] = id;
                break;
        }

        using var rejected = await client.PutAsJsonAsync(
            $"/entities/observations/{id}", invalid, cancellationToken);
        await AssertProblemAsync(rejected, HttpStatusCode.BadRequest, cancellationToken);
        using var read = await client.GetAsync($"/entities/{id}", cancellationToken);
        await Assert.That(read.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var body = await read.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var entity = body.GetProperty("properties");
        await Assert.That(body.GetProperty("references").GetProperty("observerId").GetString())
            .IsEqualTo(request["references"]!["observerId"]!.GetValue<string>());
        await Assert.That(body.GetProperty("references").GetProperty("contentId").GetString())
            .IsEqualTo(request["references"]!["contentId"]!.GetValue<string>());
        await Assert.That(entity.GetProperty("startAt").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(entity.GetProperty("endAt").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(entity.GetProperty("timeZone").ValueKind).IsEqualTo(JsonValueKind.Null);
    }

    [Test]
    public async Task ConcurrentDifferentCategoriesKeepOneAuthoritativeEntity()
    {
        var cancellationToken = TestContext.Current!.Execution.CancellationToken;
        var id = EntityId.New().Value;
        await using var factory = new HeartbeatApiFactory(_database!.ConnectionString);
        using var client = factory.CreateClient();
        var observation = NewUnknownTimeObservation();
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(index => index % 2 == 0
            ? client.PutAsJsonAsync($"/entities/observers/{id}", EntityTestRequests.Business(new { name = "Observer" }), cancellationToken)
            : client.PutAsJsonAsync($"/entities/observations/{id}", observation, cancellationToken)));
        try
        {
            await Assert.That(responses.Count(response => response.StatusCode == HttpStatusCode.Created))
                .IsEqualTo(1);
            await Assert.That(responses.Count(response => response.StatusCode == HttpStatusCode.NoContent))
                .IsEqualTo(3);
            await Assert.That(responses.Count(response => response.StatusCode == HttpStatusCode.Conflict))
                .IsEqualTo(4);
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }

        using var read = await client.GetAsync($"/entities/{id}", cancellationToken);
        await Assert.That(read.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var body = await read.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var category = body.GetProperty("category").GetString();
        await Assert.That(category is "observer" or "observation").IsTrue();
        var entity = body.GetProperty("properties");
        if (category == "observer")
        {
            await Assert.That(entity.GetProperty("name").GetString()).IsEqualTo("Observer");
            await Assert.That(entity.TryGetProperty("observerId", out _)).IsFalse();
        }
        else
        {
            await Assert.That(body.GetProperty("references").GetProperty("observerId").GetString())
                .IsEqualTo(observation["references"]!["observerId"]!.GetValue<string>());
            await Assert.That(entity.TryGetProperty("name", out _)).IsFalse();
        }
    }

    [Test]
    [Arguments("schemas/", "{\"schema\":{\"nested\":[{\"\\ud800\":1}]}}")]
    [Arguments("observers/", "{\"\\ud800\":1}")]
    [Arguments("observers/", "{\"name\":\"\\ud800\"}")]
    [Arguments("observations/", "{\"\\ud800\":1}")]
    [Arguments("schemas/", "{\"\\ud800\":1}")]
    public async Task InvalidUnicodeInAnySaveDoesNotCreateAnEntity(string route, string json)
    {
        var cancellationToken = TestContext.Current!.Execution.CancellationToken;
        var id = EntityId.New().Value;
        await using var factory = new HeartbeatApiFactory(_database!.ConnectionString);
        using var client = factory.CreateClient();
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var saved = await client.PutAsync($"/entities/{route}{id}", content, cancellationToken);
        await AssertProblemAsync(saved, HttpStatusCode.BadRequest, cancellationToken);
        using var read = await client.GetAsync($"/entities/{id}", cancellationToken);
        await AssertProblemAsync(read, HttpStatusCode.NotFound, cancellationToken);
    }

    [Test]
    [Arguments(0, false)]
    [Arguments(1, false)]
    [Arguments(1, true)]
    public async Task RequestBodyLimitIsEnforcedByKestrel(int extraBytes, bool chunked)
    {
        var cancellationToken = TestContext.Current!.Execution.CancellationToken;
        var id = EntityId.New().Value;
        await using var factory = new HeartbeatApiFactory(_database!.ConnectionString);
        factory.UseKestrel(0);
        using var client = factory.CreateClient();
        const int limit = 10 * 1024 * 1024;
        const string prefix = "{\"references\":{},\"properties\":{\"name\":\"";
        const string suffix = "\"}}";
        var json = prefix + new string('x', limit + extraBytes - prefix.Length - suffix.Length) + suffix;
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/entities/observers/{id}") { Content = content };
        request.Headers.TransferEncodingChunked = chunked;
        request.Headers.ExpectContinue = true;
        using var saved = await client.SendAsync(request, cancellationToken);
        if (extraBytes == 0)
        {
            await Assert.That(saved.StatusCode).IsEqualTo(HttpStatusCode.Created);
        }
        else
        {
            await AssertProblemAsync(saved, HttpStatusCode.RequestEntityTooLarge, cancellationToken);
            using var read = await client.GetAsync($"/entities/{id}", cancellationToken);
            await AssertProblemAsync(read, HttpStatusCode.NotFound, cancellationToken);
        }
    }

    private static JsonObject NewUnknownTimeObservation() => new()
    {
        ["references"] = new JsonObject
        {
            ["observerId"] = EntityId.New().Value.ToString(),
            ["contentId"] = EntityId.New().Value.ToString(),
            ["schemaId"] = EntityId.New().Value.ToString(),
        },
        ["properties"] = new JsonObject { ["startAt"] = null, ["endAt"] = null, ["timeZone"] = null },
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
