using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Heartbeat.Application.Entities;
using Heartbeat.Core;
using Heartbeat.Infrastructure.Database;
using Heartbeat.Testing;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Heartbeat.Integration.Tests;

public sealed class ObserverApiTests
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
    public async Task SavedObserverCanBeReadThroughTheEntityEndpoint()
    {
        var cancellationToken = TestContext.Current!.Execution.CancellationToken;
        var id = EntityId.New().Value;
        await using var factory = new HeartbeatApiFactory(_database!.ConnectionString);
        using var client = factory.CreateClient();

        using var saved = await client.PutAsJsonAsync(
            $"/entities/observers/{id}", new { references = new { }, properties = new { name = "Local observer" } }, cancellationToken);
        await Assert.That(saved.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(await saved.Content.ReadAsStringAsync(cancellationToken)).IsEqualTo("");

        using var read = await client.GetAsync($"/entities/{id}", cancellationToken);
        await Assert.That(read.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var body = await read.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        await Assert.That(body.GetProperty("category").GetString()).IsEqualTo("observer");
        var entity = body.GetProperty("properties");
        await Assert.That(entity.GetProperty("name").GetString()).IsEqualTo("Local observer");
        await Assert.That(entity.TryGetProperty("id", out _)).IsFalse();
    }

    [Test]
    public async Task RepeatedAndReplacementSavesKeepTheSameIdentity()
    {
        var cancellationToken = TestContext.Current!.Execution.CancellationToken;
        var id = EntityId.New().Value;
        await using var factory = new HeartbeatApiFactory(_database!.ConnectionString);
        using var client = factory.CreateClient();

        using var created = await client.PutAsJsonAsync(
            $"/entities/observers/{id}", new { references = new { }, properties = new { name = "Original" } }, cancellationToken);
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        using var repeated = await client.PutAsJsonAsync(
            $"/entities/observers/{id}", new { references = new { }, properties = new { name = "Original" } }, cancellationToken);
        await Assert.That(repeated.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(await repeated.Content.ReadAsStringAsync(cancellationToken)).IsEqualTo("");
        using var replaced = await client.PutAsJsonAsync(
            $"/entities/observers/{id}", new { references = new { }, properties = new { name = "Renamed" } }, cancellationToken);
        await Assert.That(replaced.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

        using var read = await client.GetAsync($"/entities/{id}", cancellationToken);
        await Assert.That(read.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var body = await read.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        await Assert.That(body.GetProperty("properties").GetProperty("name").GetString()).IsEqualTo("Renamed");
    }

    [Test]
    public async Task ConcurrentSavesCreateTheIdentityOnce()
    {
        var cancellationToken = TestContext.Current!.Execution.CancellationToken;
        var id = EntityId.New().Value;
        await using var factory = new HeartbeatApiFactory(_database!.ConnectionString);
        using var client = factory.CreateClient();

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            client.PutAsJsonAsync($"/entities/observers/{id}", new { references = new { }, properties = new { name = "Concurrent observer" } },
                cancellationToken)));
        try
        {
            await Assert.That(responses.Count(response => response.StatusCode == HttpStatusCode.Created))
                .IsEqualTo(1);
            await Assert.That(responses.Count(response => response.StatusCode == HttpStatusCode.NoContent))
                .IsEqualTo(7);
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
        await Assert.That(body.GetProperty("properties").GetProperty("name").GetString())
            .IsEqualTo("Concurrent observer");
    }

    [Test]
    public async Task CategoryConflictPreservesTheExistingEntity()
    {
        var cancellationToken = TestContext.Current!.Execution.CancellationToken;
        var id = EntityId.New();
        await using var factory = new HeartbeatApiFactory(_database!.ConnectionString);
        using var client = factory.CreateClient();
        await EntityTestRequests.RegisterAsync(client, "test-records", cancellationToken);
        using var created = await client.PutAsJsonAsync($"/entities/test-records/{id.Value}",
            EntityTestRequests.Business(new { title = "Existing window" }), cancellationToken);
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        using var saved = await client.PutAsJsonAsync(
            $"/entities/observers/{id.Value}", new { references = new { }, properties = new { name = "Replacement" } }, cancellationToken);
        await AssertProblemAsync(saved, HttpStatusCode.Conflict, cancellationToken);

        using var read = await client.GetAsync($"/entities/{id.Value}", cancellationToken);
        await Assert.That(read.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var body = await read.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        await Assert.That(body.GetProperty("category").GetString()).IsEqualTo("entity");
        await Assert.That(body.GetProperty("properties").GetProperty("title").GetString())
            .IsEqualTo("Existing window");
    }

    [Test]
    [Arguments("{}")]
    [Arguments("{\"name\":null}")]
    [Arguments("{\"name\":123}")]
    [Arguments("{\"name\":\"Observer\",\"id\":null}")]
    [Arguments("{")]
    [Arguments("")]
    public async Task InvalidSaveDoesNotCreateAnEntity(string json)
    {
        var cancellationToken = TestContext.Current!.Execution.CancellationToken;
        var id = EntityId.New().Value;
        await using var factory = new HeartbeatApiFactory(_database!.ConnectionString);
        using var client = factory.CreateClient();
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var saved = await client.PutAsync($"/entities/observers/{id}", content, cancellationToken);
        await AssertProblemAsync(saved, HttpStatusCode.BadRequest, cancellationToken);
        using var read = await client.GetAsync($"/entities/{id}", cancellationToken);
        await AssertProblemAsync(read, HttpStatusCode.NotFound, cancellationToken);
    }

    [Test]
    [Arguments("not-a-uuid")]
    [Arguments("8dcc4596-2650-4a1e-a998-53a5e7dc626a")]
    public async Task InvalidPathIdentityIsRejectedBySaveAndRead(string id)
    {
        var cancellationToken = TestContext.Current!.Execution.CancellationToken;
        await using var factory = new HeartbeatApiFactory(_database!.ConnectionString);
        using var client = factory.CreateClient();
        using var saved = await client.PutAsJsonAsync(
            $"/entities/observers/{id}", new { references = new { }, properties = new { name = "Observer" } }, cancellationToken);
        await AssertProblemAsync(saved, HttpStatusCode.BadRequest, cancellationToken);
        using var read = await client.GetAsync($"/entities/{id}", cancellationToken);
        await AssertProblemAsync(read, HttpStatusCode.BadRequest, cancellationToken);
    }

    [Test]
    public async Task FailedContentWriteRollsBackTheIdentity()
    {
        var cancellationToken = TestContext.Current!.Execution.CancellationToken;
        // Simulate a database write failure after index insertion in this isolated database.
        await ArrangeDatabaseAsync(dbContext => dbContext.Database.ExecuteSqlRawAsync(
            "ALTER TABLE observers RENAME TO unavailable_observers", cancellationToken));

        var id = EntityId.New().Value;
        await using var factory = new HeartbeatApiFactory(_database!.ConnectionString);
        using var client = factory.CreateClient();
        using var saved = await client.PutAsJsonAsync(
            $"/entities/observers/{id}", new { references = new { }, properties = new { name = "Observer" } }, cancellationToken);
        await AssertProblemAsync(saved, HttpStatusCode.InternalServerError, cancellationToken);
        using var read = await client.GetAsync($"/entities/{id}", cancellationToken);
        await AssertProblemAsync(read, HttpStatusCode.NotFound, cancellationToken);
    }

    [Test]
    public async Task ObservationReadsReturnUtcAndPreserveUnknownBoundaries()
    {
        var cancellationToken = TestContext.Current!.Execution.CancellationToken;
        var id = EntityId.New();
        var observerId = EntityId.New();
        var contentId = EntityId.New();
        var schemaId = EntityId.New();
        await ArrangeDatabaseAsync(async dbContext =>
        {
            dbContext.Entities.Add(new EntityIndexRow { Id = id, TableName = "observations" });
            dbContext.Observations.Add(new Observation
            {
                Id = id,
                ObserverId = observerId,
                ContentId = contentId,
                SchemaId = schemaId,
                StartAt = DateTimeOffset.Parse("2026-10-06T09:00:00.123456+08:00",
                    CultureInfo.InvariantCulture),
                EndAt = null,
                TimeZone = "Asia/Shanghai",
            });
            await dbContext.SaveChangesAsync(cancellationToken);
        });

        await using var factory = new HeartbeatApiFactory(_database!.ConnectionString);
        using var client = factory.CreateClient();
        using var read = await client.GetAsync($"/entities/{id.Value}", cancellationToken);
        await Assert.That(read.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var body = await read.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        await Assert.That(body.GetProperty("category").GetString()).IsEqualTo("observation");
        var entity = body.GetProperty("properties");
        await Assert.That(entity.GetProperty("startAt").GetString())
            .IsEqualTo("2026-10-06T01:00:00.123456Z");
        await Assert.That(entity.GetProperty("endAt").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(entity.TryGetProperty("id", out _)).IsFalse();
        await Assert.That(body.GetProperty("references").GetProperty("observerId").GetString()).IsEqualTo(observerId.Value.ToString());
        await Assert.That(body.GetProperty("references").GetProperty("contentId").GetString()).IsEqualTo(contentId.Value.ToString());
        await Assert.That(body.GetProperty("references").GetProperty("schemaId").GetString()).IsEqualTo(schemaId.Value.ToString());
        await Assert.That(entity.GetProperty("timeZone").GetString()).IsEqualTo("Asia/Shanghai");
    }

    private async Task ArrangeDatabaseAsync(Func<HeartbeatDbContext, Task> arrange)
    {
        await using var dataSource = NpgsqlDataSource.Create(_database!.ConnectionString);
        var options = new DbContextOptionsBuilder<HeartbeatDbContext>().UseNpgsql(dataSource).Options;
        await using var dbContext = new HeartbeatDbContext(options);
        await arrange(dbContext);
    }

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
        await Assert.That(string.IsNullOrWhiteSpace(problem.GetProperty("title").GetString())).IsFalse();
        await Assert.That(string.IsNullOrWhiteSpace(problem.GetProperty("detail").GetString())).IsFalse();
    }
}
