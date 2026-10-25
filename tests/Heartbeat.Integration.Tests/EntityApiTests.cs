using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Heartbeat.Application.Entities;
using Heartbeat.Core;
using Heartbeat.Infrastructure.Database;
using Npgsql;

namespace Heartbeat.Integration.Tests;

public sealed class EntityApiTests
{
    internal static VersionSubmission Schema(string resource = "items", string fields = "{}") => new(EntityId.New(), VersionId.New(), null, null,
        new EntitySchema("Items", "Items", resource, JsonSerializer.Deserialize<JsonElement>(fields)));
    internal static VersionSubmission Business(VersionSubmission schema, string content = "{}") => new(EntityId.New(), VersionId.New(), null, schema.VersionId,
        new BusinessContent(JsonSerializer.Deserialize<JsonElement>(content)));
    internal static Task<HttpResponseMessage> Submit(HttpClient http, params VersionSubmission[] versions) =>
        http.PostAsJsonAsync("/versions", new { versions = versions.Select(VersionJson.Submission) }, TestContext.Current!.Execution.CancellationToken);
    internal static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());

    [Test]
    public async Task SevenOperationsPreserveChainRetryDeletionAndResourceReuse()
    {
        await using var database = await DatabaseFixture.CreateAsync();
        await using var factory = new HeartbeatApiFactory(database.ConnectionString);
        using var http = factory.CreateClient();
        var schema = Schema(); var first = Business(schema);
        var second = first with { VersionId = VersionId.New(), PreviousVersionId = first.VersionId };
        using var submitted = await Submit(http, schema, first, second);
        await Assert.That(submitted.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var saved = await Json(submitted);
        using var retried = await Submit(http, first);
        await Assert.That((await Json(retried)).GetProperty("versions")[0].GetProperty("recordedAt").GetString())
            .IsEqualTo(saved.GetProperty("versions")[1].GetProperty("recordedAt").GetString());
        using var read = await http.GetAsync($"/entities/{first.EntityId.Value}");
        await Assert.That((await Json(read)).GetProperty("versionId").GetGuid()).IsEqualTo(second.VersionId.Value);
        using var chain = await http.GetAsync($"/entities/{first.EntityId.Value}/versions");
        await Assert.That((await Json(chain)).GetProperty("versions").GetArrayLength()).IsEqualTo(2);
        using var list = await http.GetAsync("/entities?resourceName=items");
        await Assert.That((await Json(list)).GetProperty("versions").GetArrayLength()).IsEqualTo(1);
        using var blocked = await http.DeleteAsync($"/entities/{schema.EntityId.Value}");
        await Assert.That((await Json(blocked)).GetProperty("rule").GetString()).IsEqualTo("HISTORY-9");
        using var deleted = await http.DeleteAsync($"/entities/{first.EntityId.Value}");
        using var again = await http.DeleteAsync($"/entities/{first.EntityId.Value}");
        await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(again.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        foreach (var path in new[] { $"/entities/{first.EntityId.Value}", $"/entities/{first.EntityId.Value}/versions", $"/versions/{first.VersionId.Value}", $"/versions/{second.VersionId.Value}" })
        {
            using var gone = await http.GetAsync(path);
            await Assert.That(gone.StatusCode).IsEqualTo(HttpStatusCode.Gone);
        }
        using var replay = await Submit(http, first);
        await Assert.That((await Json(replay)).GetProperty("rule").GetString()).IsEqualTo("HISTORY-7");
        using var reused = await Submit(http, first with { EntityId = EntityId.New() });
        await Assert.That((await Json(reused)).GetProperty("rule").GetString()).IsEqualTo("VERSION-9");
        using var removedSchema = await http.DeleteAsync($"/entities/{schema.EntityId.Value}");
        using var newSchema = await Submit(http, Schema());
        await Assert.That(newSchema.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var unknown = await http.DeleteAsync($"/entities/{EntityId.New().Value}");
        await Assert.That(unknown.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task MalformedSuffixDoesNotSavePrefixButRuleRejectionDoes()
    {
        await using var database = await DatabaseFixture.CreateAsync();
        await using var factory = new HeartbeatApiFactory(database.ConnectionString);
        using var http = factory.CreateClient();
        var first = Schema();
        var invalid = VersionJson.Submission(Schema("other")).GetRawText();
        foreach (var suffix in new[] { invalid[..^1] + ",\"recordedAt\":null}", "{}", invalid.Replace("\"content\":", "\"extra\":null,\"content\":", StringComparison.Ordinal) })
        {
            using var body = new StringContent("{\"versions\":[" + VersionJson.Submission(first).GetRawText() + "," + suffix + "]}", Encoding.UTF8, "application/json");
            using var bad = await http.PostAsync("/versions", body);
            await Assert.That(bad.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            await Assert.That((await Json(bad)).TryGetProperty("index", out _)).IsFalse();
            using var absent = await http.GetAsync($"/versions/{first.VersionId.Value}");
            await Assert.That(absent.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        }
        var rejected = new VersionSubmission(EntityId.New(), VersionId.New(), null, null, new InputSet([]));
        var later = Schema("later");
        using var partial = await Submit(http, first, rejected, later);
        var problem = await Json(partial);
        await Assert.That(problem.GetProperty("index").GetInt32()).IsEqualTo(1);
        await Assert.That(problem.GetProperty("versionId").GetGuid()).IsEqualTo(rejected.VersionId.Value);
        await Assert.That(problem.GetProperty("rule").GetString()).IsEqualTo("INPUTSET-2");
        using var present = await http.GetAsync($"/versions/{first.VersionId.Value}");
        using var absentLater = await http.GetAsync($"/versions/{later.VersionId.Value}");
        await Assert.That(present.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(absentLater.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task QueryUsesKnownBoundariesCurrentProjectionAndStablePagination()
    {
        await using var database = await DatabaseFixture.CreateAsync();
        await using var factory = new HeartbeatApiFactory(database.ConnectionString);
        using var http = factory.CreateClient();
        var schema = Schema(); var business = Business(schema);
        var t = new DateTimeOffset(1999, 12, 31, 23, 59, 59, TimeSpan.Zero);
        VersionSubmission ObservationAt(DateTimeOffset? start, DateTimeOffset? end) => new(EntityId.New(), VersionId.New(), null, null,
            new Observation(business.VersionId, business.VersionId, null, start, end, "Asia/Shanghai"));
        var point = ObservationAt(t.AddTicks(9), t.AddTicks(9));
        var before = ObservationAt(t.AddSeconds(-1), t);
        var unknown = ObservationAt(null, null);
        var endOnly = ObservationAt(null, t);
        var startOnly = ObservationAt(t, null);
        using var saved = await Submit(http, schema, business, point, before, unknown, endOnly, startOnly);
        await Assert.That(saved.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var read = await http.GetAsync($"/versions/{point.VersionId.Value}");
        await Assert.That((await Json(read)).GetProperty("content").GetProperty("startAt").GetString()).IsEqualTo("1999-12-31T23:59:59.000000Z");
        var raw = VersionJson.Submission(point).GetRawText().Replace("1999-12-31T23:59:59.000000Z", "2000-01-01T07:59:59.0000009+08:00", StringComparison.Ordinal);
        using var body = new StringContent("{\"versions\":[" + raw + "]}", Encoding.UTF8, "application/json");
        using var retry = await http.PostAsync("/versions", body);
        await Assert.That(retry.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var query = "/observations?from=1999-12-31T23:59:59Z&to=2000-01-01T00:00:00Z&resourceName=items&limit=1";
        var found = new List<Guid>(); string? next = null;
        do
        {
            using var page = await http.GetAsync(query + (next is null ? "" : "&after=" + Uri.EscapeDataString(next)));
            var json = await Json(page);
            found.AddRange(json.GetProperty("versions").EnumerateArray().Select(version => version.GetProperty("entityId").GetGuid()));
            next = json.GetProperty("next").GetString();
        } while (next is not null);
        await Assert.That(found.SequenceEqual(new[] { point.EntityId.Value, endOnly.EntityId.Value, startOnly.EntityId.Value }.Order())).IsTrue();
        using var moved = await Submit(http, point with { VersionId = VersionId.New(), PreviousVersionId = point.VersionId, Content = new Observation(business.VersionId, business.VersionId, null, null, null, null) });
        using var remaining = await http.GetAsync(query.Replace("limit=1", "limit=100", StringComparison.Ordinal));
        await Assert.That((await Json(remaining)).GetProperty("versions").GetArrayLength()).IsEqualTo(2);
        using var missing = await http.GetAsync("/entities?resourceName=missing");
        await Assert.That(missing.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        using var incompatible = await http.GetAsync("/entities?category=observation&resourceName=items");
        await Assert.That((await Json(incompatible)).GetProperty("versions").GetArrayLength()).IsEqualTo(0);
    }

    [Test]
    public async Task ConcurrentFirstVersionsFromTwoServicesConflict()
    {
        await using var database = await DatabaseFixture.CreateAsync();
        await using var data = NpgsqlDataSource.Create(database.ConnectionString);
        var schema = Schema();
        var one = new EntityService(new PostgresEntityStore(data, TimeProvider.System), TimeProvider.System);
        var other = new EntityService(new PostgresEntityStore(data, TimeProvider.System), TimeProvider.System);
        var writes = await Task.WhenAll(one.SubmitAsync([schema]), other.SubmitAsync([schema with { VersionId = VersionId.New() }]));
        await Assert.That(writes.Count(result => result.Rejection is null)).IsEqualTo(1);
        await Assert.That(writes.Single(result => result.Rejection is not null).Rejection!.Rule).IsEqualTo("VERSION-6");
    }
}
