using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Orisumi.Application.Entities;
using Orisumi.Core;
using Orisumi.Infrastructure.Database;
using Npgsql;

namespace Orisumi.Integration.Tests;

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
    [Property("caseId", "api-submission")]
    public async Task SubmissionConfirmsInOrderRetriesAndRejectsConflictingVersions()
    {
        await using var database = await DatabaseFixture.CreateAsync();
        await using var factory = new OrisumiApiFactory(database.ConnectionString);
        using var http = factory.CreateClient();
        var schema = Schema(fields: """{"name":{"type":"string","description":"Test field value.","required":true,"nullable":false}}""");
        var first = Business(schema, """{"name":"first"}""");
        var second = first with { VersionId = VersionId.New(), PreviousVersionId = first.VersionId };
        using var submitted = await Submit(http, schema, first, second);
        await Assert.That(submitted.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var saved = (await Json(submitted)).GetProperty("versions").EnumerateArray().ToArray();
        await Assert.That(saved.Select(v => v.GetProperty("versionId").GetGuid())
            .SequenceEqual(new[] { schema.VersionId.Value, first.VersionId.Value, second.VersionId.Value })).IsTrue();
        foreach (var confirmation in saved)
            await Assert.That(confirmation.GetProperty("recordedAt").GetDateTimeOffset()).IsNotEqualTo(default(DateTimeOffset));
        using var retried = await Submit(http, first);
        await Assert.That(retried.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await Json(retried)).GetProperty("versions")[0].GetProperty("recordedAt").GetString())
            .IsEqualTo(saved[1].GetProperty("recordedAt").GetString());

        var time = new DateTimeOffset(2026, 10, 6, 1, 0, 0, TimeSpan.Zero);
        var observation = new VersionSubmission(EntityId.New(), VersionId.New(), null, null,
            new Observation(first.VersionId, first.VersionId, null, time, time, null));
        using var observed = await Submit(http, observation);
        await Assert.That(observed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var offset = VersionJson.Submission(observation).GetRawText().Replace(
            "2026-10-06T01:00:00.000000Z", "2026-10-06T09:00:00+08:00", StringComparison.Ordinal);
        using var equivalent = await SubmitRaw(http, offset);
        await Assert.That(equivalent.StatusCode).IsEqualTo(HttpStatusCode.OK);

        using var changed = await Submit(http, first with { Content = new BusinessContent(JsonSerializer.Deserialize<JsonElement>("""{"name":"changed"}""")) });
        await Conflict(changed, "VERSION-7");
        using var stale = await Submit(http, second with { VersionId = VersionId.New() });
        await Conflict(stale, "VERSION-6");
    }

    [Test]
    [Property("caseId", "api-submission")]
    public async Task ContractErrorSavesNothingAndRuleRejectionSavesOnlyPrefix()
    {
        await using var database = await DatabaseFixture.CreateAsync();
        await using var factory = new OrisumiApiFactory(database.ConnectionString);
        using var http = factory.CreateClient();
        var first = Schema();
        using var bad = await SubmitRaw(http, VersionJson.Submission(first).GetRawText(), WithRecordedAt(Schema("other")));
        await Assert.That(bad.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That((await Json(bad)).TryGetProperty("index", out _)).IsFalse();
        using var absent = await http.GetAsync($"/versions/{first.VersionId.Value}");
        await Assert.That(absent.StatusCode).IsEqualTo(HttpStatusCode.NotFound);

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
    [Property("caseId", "api-submission")]
    public async Task RequestLargerThanTenMiBIsRejected()
    {
        await using var database = await DatabaseFixture.CreateAsync();
        await using var factory = new OrisumiApiFactory(database.ConnectionString);
        using var http = factory.CreateClient();
        var schema = Schema();
        using var response = await Submit(http, schema with
        {
            Content = new EntitySchema("Items", new string('x', 10 * 1024 * 1024), "items", JsonSerializer.SerializeToElement(new { })),
        });
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.RequestEntityTooLarge);
    }

    [Test]
    [Property("caseId", "api-reading")]
    public async Task ReadingReturnsCompleteVersionsOrderedChainAndUtcMicroseconds()
    {
        await using var database = await DatabaseFixture.CreateAsync();
        await using var factory = new OrisumiApiFactory(database.ConnectionString);
        using var http = factory.CreateClient();
        var schema = Schema(); var first = Business(schema);
        var second = first with { VersionId = VersionId.New(), PreviousVersionId = first.VersionId };
        var third = second with { VersionId = VersionId.New(), PreviousVersionId = second.VersionId };
        using var saved = await Submit(http, schema, first, second, third);
        await Assert.That(saved.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var read = await http.GetAsync($"/versions/{first.VersionId.Value}");
        await Assert.That(read.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var version = await Json(read);
        await Assert.That(version.EnumerateObject().Select(p => p.Name).ToHashSet().SetEquals(
            ["entityId", "versionId", "previousVersionId", "category", "schemaVersionId", "recordedAt", "content"])).IsTrue();
        await Assert.That(version.GetProperty("entityId").GetGuid()).IsEqualTo(first.EntityId.Value);
        await Assert.That(version.GetProperty("versionId").GetGuid()).IsEqualTo(first.VersionId.Value);
        await Assert.That(version.GetProperty("previousVersionId").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(version.GetProperty("category").GetString()).IsEqualTo("business");
        await Assert.That(version.GetProperty("schemaVersionId").GetGuid()).IsEqualTo(schema.VersionId.Value);
        await Assert.That(JsonElement.DeepEquals(version.GetProperty("content"), ((BusinessContent)first.Content).Fields)).IsTrue();
        await Assert.That(version.GetProperty("recordedAt").GetString()!.EndsWith('Z')).IsTrue();
        using var current = await http.GetAsync($"/entities/{first.EntityId.Value}");
        await Assert.That((await Json(current)).GetProperty("versionId").GetGuid()).IsEqualTo(third.VersionId.Value);
        using var chain = await http.GetAsync($"/entities/{first.EntityId.Value}/versions");
        await Assert.That((await Json(chain)).GetProperty("versions").EnumerateArray().Select(v => v.GetProperty("versionId").GetGuid())
            .SequenceEqual(new[] { first.VersionId.Value, second.VersionId.Value, third.VersionId.Value })).IsTrue();

        var observation = new VersionSubmission(EntityId.New(), VersionId.New(), null, null,
            new Observation(first.VersionId, first.VersionId, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddSeconds(1), null));
        var raw = VersionJson.Submission(observation).GetRawText()
            .Replace("1970-01-01T00:00:00.000000Z", "2026-10-06T09:00:00+08:00", StringComparison.Ordinal)
            .Replace("1970-01-01T00:00:01.000000Z", "2026-10-06T09:00:01.1234567+08:00", StringComparison.Ordinal);
        using var observed = await SubmitRaw(http, raw);
        await Assert.That(observed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var normalized = await http.GetAsync($"/versions/{observation.VersionId.Value}");
        var content = (await Json(normalized)).GetProperty("content");
        await Assert.That(content.GetProperty("startAt").GetString()).IsEqualTo("2026-10-06T01:00:00.000000Z");
        await Assert.That(content.GetProperty("endAt").GetString()).IsEqualTo("2026-10-06T01:00:01.123456Z");
        foreach (var path in new[] { $"/versions/{VersionId.New().Value}", $"/entities/{EntityId.New().Value}" })
        {
            using var missing = await http.GetAsync(path);
            await Assert.That(missing.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        }
    }

    [Test]
    [Property("caseId", "api-deletion")]
    public async Task DeletionRemovesAllVersionsPreventsReplayAndAllowsResourceReuse()
    {
        await using var database = await DatabaseFixture.CreateAsync();
        await using var factory = new OrisumiApiFactory(database.ConnectionString);
        using var http = factory.CreateClient();
        var schema = Schema(); var first = Business(schema);
        var second = first with { VersionId = VersionId.New(), PreviousVersionId = first.VersionId };
        using var saved = await Submit(http, schema, first, second);
        await Assert.That(saved.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var blocked = await http.DeleteAsync($"/entities/{schema.EntityId.Value}");
        await Conflict(blocked, "HISTORY-9");
        using var deleted = await http.DeleteAsync($"/entities/{first.EntityId.Value}");
        await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        foreach (var path in new[] { $"/entities/{first.EntityId.Value}", $"/entities/{first.EntityId.Value}/versions", $"/versions/{first.VersionId.Value}", $"/versions/{second.VersionId.Value}" })
        {
            using var gone = await http.GetAsync(path);
            await Assert.That(gone.StatusCode).IsEqualTo(HttpStatusCode.Gone);
        }
        using var again = await http.DeleteAsync($"/entities/{first.EntityId.Value}");
        await Assert.That(again.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        using var replay = await Submit(http, first);
        await Conflict(replay, "HISTORY-7");
        using var reused = await Submit(http, first with { EntityId = EntityId.New() });
        await Conflict(reused, "VERSION-9");
        using var removedSchema = await http.DeleteAsync($"/entities/{schema.EntityId.Value}");
        await Assert.That(removedSchema.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        using var newSchema = await Submit(http, Schema());
        await Assert.That(newSchema.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var unknown = await http.DeleteAsync($"/entities/{EntityId.New().Value}");
        await Assert.That(unknown.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    [Property("caseId", "api-errors")]
    public async Task ErrorsHaveProblemDetailsAndConflictIdentifiesRejectedVersion()
    {
        await using var database = await DatabaseFixture.CreateAsync();
        await using var factory = new OrisumiApiFactory(database.ConnectionString);
        using var http = factory.CreateClient();
        var schema = Schema();
        using var bad = await SubmitRaw(http, WithRecordedAt(schema));
        await Problem(bad, HttpStatusCode.BadRequest);
        using var missing = await http.GetAsync($"/entities/{EntityId.New().Value}");
        await Problem(missing, HttpStatusCode.NotFound);
        using var saved = await Submit(http, schema);
        await Assert.That(saved.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var conflict = await Submit(http, schema with { Content = new EntitySchema("Items", "Changed", "items", JsonSerializer.SerializeToElement(new { })) });
        await Problem(conflict, HttpStatusCode.Conflict);
        var problem = await Json(conflict);
        await Assert.That(problem.GetProperty("rule").GetString()).IsEqualTo("VERSION-7");
        await Assert.That(problem.GetProperty("index").GetInt32()).IsEqualTo(0);
        await Assert.That(problem.GetProperty("versionId").GetGuid()).IsEqualTo(schema.VersionId.Value);
    }

    [Test]
    [Property("caseId", "api-lists")]
    public async Task ListsFilterCategoriesAndOrderCurrentKnownBoundariesAcrossPages()
    {
        await using var database = await DatabaseFixture.CreateAsync();
        await using var factory = new OrisumiApiFactory(database.ConnectionString);
        using var http = factory.CreateClient();
        var schema = Schema(); var business = Business(schema);
        var t = DateTimeOffset.UnixEpoch;
        VersionSubmission ObservationAt(DateTimeOffset? start, DateTimeOffset? end) => new(EntityId.New(), VersionId.New(), null, null,
            new Observation(business.VersionId, business.VersionId, null, start, end, null));
        var point = ObservationAt(t.AddSeconds(2), t.AddSeconds(2));
        var overlap = ObservationAt(t.AddSeconds(-1), t.AddSeconds(1));
        var unknown = ObservationAt(null, null);
        var endOnly = ObservationAt(null, t.AddSeconds(1));
        var startOnly = ObservationAt(t.AddSeconds(2), null);
        using var saved = await Submit(http, schema, business, point, overlap, unknown, endOnly, startOnly);
        await Assert.That(saved.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var items = await http.GetAsync("/entities?resourceName=items");
        await Assert.That((await Json(items)).GetProperty("versions").EnumerateArray().Single().GetProperty("versionId").GetGuid()).IsEqualTo(business.VersionId.Value);
        using var schemas = await http.GetAsync("/entities?category=entity_schema");
        await Assert.That((await Json(schemas)).GetProperty("versions").EnumerateArray().Single().GetProperty("versionId").GetGuid()).IsEqualTo(schema.VersionId.Value);
        using var missing = await http.GetAsync("/entities?resourceName=missing");
        await Assert.That(missing.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        const string query = "/observations?from=1970-01-01T00:00:00Z&to=1970-01-01T00:00:03Z&resourceName=items";
        using var all = await http.GetAsync(query);
        var expected = new[] { overlap.EntityId.Value, endOnly.EntityId.Value }
            .Concat(new[] { point.EntityId.Value, startOnly.EntityId.Value }.Order()).ToArray();
        await Assert.That((await Json(all)).GetProperty("versions").EnumerateArray().Select(v => v.GetProperty("entityId").GetGuid()).SequenceEqual(expected)).IsTrue();
        using var moved = await Submit(http, point with { VersionId = VersionId.New(), PreviousVersionId = point.VersionId,
            Content = ((Observation)point.Content) with { StartAt = t.AddHours(1), EndAt = t.AddHours(1) } });
        await Assert.That(moved.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var remaining = await http.GetAsync(query);
        var remainingIds = (await Json(remaining)).GetProperty("versions").EnumerateArray().Select(v => v.GetProperty("entityId").GetGuid()).ToArray();
        await Assert.That(remainingIds.SequenceEqual(expected.Where(id => id != point.EntityId.Value))).IsTrue();
        var found = new List<Guid>(); string? next = null;
        do
        {
            using var page = await http.GetAsync(query + "&limit=1" + (next is null ? "" : "&after=" + Uri.EscapeDataString(next)));
            var json = await Json(page);
            found.AddRange(json.GetProperty("versions").EnumerateArray().Select(v => v.GetProperty("entityId").GetGuid()));
            next = json.GetProperty("next").GetString();
            await Assert.That(found.Count <= remainingIds.Length).IsTrue();
        } while (next is not null);
        await Assert.That(found.SequenceEqual(remainingIds)).IsTrue();
    }

    private static string WithRecordedAt(VersionSubmission version) => VersionJson.Submission(version).GetRawText()[..^1] + ",\"recordedAt\":null}";

    private static async Task<HttpResponseMessage> SubmitRaw(HttpClient http, params string[] versions)
    {
        using var body = new StringContent("{\"versions\":[" + string.Join(',', versions) + "]}", Encoding.UTF8, "application/json");
        return await http.PostAsync("/versions", body);
    }

    private static async Task Conflict(HttpResponseMessage response, string rule)
    {
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That((await Json(response)).GetProperty("rule").GetString()).IsEqualTo(rule);
    }

    private static async Task Problem(HttpResponseMessage response, HttpStatusCode status)
    {
        await Assert.That(response.StatusCode).IsEqualTo(status);
        await Assert.That(response.Content.Headers.ContentType!.MediaType).IsEqualTo("application/problem+json");
        var json = await Json(response);
        await Assert.That(json.GetProperty("type").GetString()).IsEqualTo("about:blank");
        await Assert.That(string.IsNullOrWhiteSpace(json.GetProperty("title").GetString())).IsFalse();
        await Assert.That(json.GetProperty("status").GetInt32()).IsEqualTo((int)status);
        await Assert.That(string.IsNullOrWhiteSpace(json.GetProperty("detail").GetString())).IsFalse();
    }

    [Test]
    [Property("caseId", "storage-transactions")]
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
