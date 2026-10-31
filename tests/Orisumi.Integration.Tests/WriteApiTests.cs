using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Orisumi.Core;
using Orisumi.Testing;

namespace Orisumi.Integration.Tests;

public sealed class WriteApiTests
{
    internal const string NameFields = """{"name":{"type":"string","description":"Name","required":true,"nullable":false}}""";
    internal static readonly object UnknownTime = new { startAt = (string?)null, endAt = (string?)null, timeZone = (string?)null };

    [Test, Property("caseId", "api-writes")]
    public async Task RecognitionVersionsObservationsAndSequenceFollowTheWriteContract()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var factory = new OrisumiApiFactory(db.ConnectionString);
        using var http = factory.CreateClient();
        var writer = new WriteClient(http); await writer.InitializeAsync();
        var schema = await writer.DeclareAsync(fields: NameFields);
        var first = await writer.WriteAsync(schema, new { name = "one" }, time: UnknownTime);
        var second = await writer.WriteAsync(schema, new { name = "one" }, time: UnknownTime);
        await Assert.That(first.EntityId != second.EntityId).IsTrue();
        using var readings = await http.GetAsync("/observations");
        await Assert.That((await WriteClient.JsonAsync(readings)).GetProperty("versions").GetArrayLength()).IsEqualTo(2);
        var keyed = await writer.WriteAsync(schema, new { name = "one" }, "key", time: UnknownTime);
        var same = await writer.SendAsync([WriteClient.Write(schema, new { name = "one" }, "key", time: UnknownTime)]);
        await Assert.That(same.GetProperty("results")[0].GetProperty("changed").GetBoolean()).IsFalse();
        var observation = VersionJson.ReadSaved(same.GetProperty("results")[0].GetProperty("observation"));
        var repeated = await writer.SendAsync([WriteClient.Write(schema, new { name = "one" }, "key", time: UnknownTime)]);
        await Assert.That(repeated.GetProperty("results")[0].GetProperty("observation").GetProperty("versionId").GetGuid()).IsEqualTo(observation.VersionId.Value);
        var changed = await writer.WriteAsync(schema, new { name = "two" }, "key");
        var reverted = await writer.WriteAsync(schema, new { name = "one" }, "key");
        await Assert.That(changed.PreviousVersionId).IsEqualTo(keyed.VersionId);
        await Assert.That(reverted.PreviousVersionId).IsEqualTo(changed.VersionId);
        var timed = await writer.SendAsync([WriteClient.Write(schema, new { name = "one" }, "key", time: new { startAt = "2026-10-31T00:00:00Z", endAt = (string?)null, timeZone = "Asia/Shanghai" })]);
        await Assert.That(timed.GetProperty("results")[0].GetProperty("observation").GetProperty("previousVersionId").GetGuid()).IsEqualTo(observation.VersionId.Value);
        var sequenced = await writer.SendAsync([WriteClient.Write(schema, new { name = "temperature" })], sequence: 10);
        await Assert.That(sequenced.GetProperty("results").GetArrayLength()).IsEqualTo(1);
        foreach (var sequence in new long[] { 10, 9 })
        {
            var duplicate = await writer.SendAsync([WriteClient.Write(schema, new { name = "temperature" })], sequence: sequence);
            await Assert.That(duplicate.GetProperty("duplicate").GetBoolean()).IsTrue();
            await Assert.That(duplicate.GetProperty("results").GetArrayLength()).IsEqualTo(0);
        }
    }

    [Test, Property("caseId", "api-writes")]
    public async Task WrongShapeOnAnExistingEntityAndLongKeysRejectOnlyThatWrite()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var factory = new OrisumiApiFactory(db.ConnectionString);
        using var http = factory.CreateClient();
        var writer = new WriteClient(http); await writer.InitializeAsync();
        var fields = """
            {"place":{"type":"object","description":"Place","required":true,"nullable":false,"fields":{"name":{"type":"string","description":"Name","required":true,"nullable":false}}},
             "tags":{"type":"array","description":"Tags","required":true,"nullable":false,"items":{"type":"string","description":"Tag","nullable":false}}}
            """;
        var schema = await writer.DeclareAsync(fields: fields);
        var tags = new[] { "one" };
        var good = new { place = new { name = "here" }, tags };
        await writer.WriteAsync(schema, good, "existing");
        var longKey = new string('k', 2049);
        var results = (await writer.SendAsync([
            WriteClient.Write(schema, new { place = "here", tags }, "existing"),
            WriteClient.Write(schema, new { place = new { name = "here" }, tags = new { one = 1 } }, "existing"),
            WriteClient.Write(schema, good, longKey),
            WriteClient.Write(schema, good, "after"),
        ])).GetProperty("results");
        await Assert.That(results[0].GetProperty("error").GetProperty("rule").GetString()).IsEqualTo("SCHEMA-2");
        await Assert.That(results[1].GetProperty("error").GetProperty("rule").GetString()).IsEqualTo("SCHEMA-2");
        await Assert.That(results[2].GetProperty("error").GetProperty("status").GetInt32()).IsEqualTo(400);
        await Assert.That(results[3].GetProperty("changed").GetBoolean()).IsTrue();
        var longest = new string('k', 2048 - "test".Length);
        await Assert.That((await writer.SendAsync([WriteClient.Write(schema, good, longest)])).GetProperty("results")[0].GetProperty("changed").GetBoolean()).IsTrue();
    }

    [Test, Property("caseId", "storage-transactions"), Property("caseId", "api-writes")]
    public async Task RejectionRollsBackOnlyItsWriteIncludingKeysInputsAndContent()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var factory = new OrisumiApiFactory(db.ConnectionString);
        using var http = factory.CreateClient();
        var writer = new WriteClient(http); await writer.InitializeAsync();
        var schema = await writer.DeclareAsync(fields: NameFields);
        var invalid = new { startAt = "2026-10-31T02:00:00Z", endAt = "2026-10-31T01:00:00Z", timeZone = (string?)null };
        var results = (await writer.SendAsync([
            WriteClient.Write(schema, new { name = "before" }, "before"),
            WriteClient.Write(schema, new { name = "reject" }, "retry", time: invalid, inputs: [writer.Observer.VersionId]),
            WriteClient.Write(schema, new { name = "after" }, "after", time: UnknownTime, inputs: [writer.Observer.VersionId]),
            WriteClient.Write(EntityId.New(), new { }),
            WriteClient.Write(schema, new { name = "nul\0" }),
            WriteClient.Write(schema, new { name = "last" })], sequence: 1)).GetProperty("results");
        await Assert.That(results[1].GetProperty("error").GetProperty("rule").GetString()).IsEqualTo("OBSERVATION-6");
        await Assert.That(results[3].GetProperty("error").GetProperty("rule").GetString()).IsEqualTo("SCHEMA-3");
        await Assert.That(results[4].GetProperty("error").GetProperty("status").GetInt32()).IsEqualTo(400);
        await Assert.That(results[5].GetProperty("changed").GetBoolean()).IsTrue();
        await Assert.That(await BinaryContentTests.Count(db.ConnectionString, "recognition_keys")).IsEqualTo(3);
        await Assert.That(await BinaryContentTests.Count(db.ConnectionString, "observations")).IsEqualTo(1);
        using var inputs = await http.GetAsync("/entities?category=input_set");
        await Assert.That((await WriteClient.JsonAsync(inputs)).GetProperty("versions").GetArrayLength()).IsEqualTo(1);
        var corrected = await writer.WriteAsync(schema, new { name = "reject" }, "retry", time: UnknownTime);
        await Assert.That(corrected.PreviousVersionId).IsNull();
        var duplicate = await writer.SendAsync([], sequence: 1);
        await Assert.That(duplicate.GetProperty("duplicate").GetBoolean()).IsTrue();
    }

    [Test, Property("caseId", "api-write-references")]
    public async Task ReferencesResolveInOrderAndCompareTargetEntitiesAcrossVersions()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var factory = new OrisumiApiFactory(db.ConnectionString);
        using var http = factory.CreateClient();
        var writer = new WriteClient(http); await writer.InitializeAsync();
        var schema = await writer.DeclareAsync(fields: NameFields);
        var links = await writer.DeclareAsync("links", """
            {"targetVersionId":{"type":"reference","description":"Target","required":true,"nullable":false},
             "optionalVersionId":{"type":"reference","description":"Optional","required":true,"nullable":true},
             "text":{"type":"string","description":"Literal","required":true,"nullable":false},
             "nested":{"type":"array","description":"Links","required":true,"nullable":false,"items":{"type":"object","description":"Link","nullable":false,"fields":{"targetVersionId":{"type":"reference","description":"Target","required":true,"nullable":false}}}}}
            """);
        var target = await writer.WriteAsync(schema, new { name = "first" }, "target");
        object Content(object reference) => new { targetVersionId = reference, optionalVersionId = new { entityId = Guid.CreateVersion7() }, text = target.VersionId.Value.ToString(), nested = new[] { new { targetVersionId = reference } } };
        var original = await writer.WriteAsync(links, Content(target.VersionId.Value), "link");
        var next = await writer.WriteAsync(schema, new { name = "second" }, "target");
        var same = await writer.WriteAsync(links, Content(new { entityId = target.EntityId.Value }), "link");
        await Assert.That(same.VersionId).IsEqualTo(original.VersionId);
        var byKey = await writer.WriteAsync(links, Content(new { schemaId = schema.Value, key = new { scope = (string?)null, scheme = "test", value = "target" } }));
        var fields = ((BusinessContent)byKey.Content).Fields;
        await Assert.That(fields.GetProperty("targetVersionId").GetGuid()).IsEqualTo(next.VersionId.Value);
        await Assert.That(fields.GetProperty("optionalVersionId").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(fields.GetProperty("text").GetString()).IsEqualTo(target.VersionId.Value.ToString());
        using var deleted = await http.DeleteAsync($"/entities/{target.EntityId.Value}"); deleted.EnsureSuccessStatusCode();
        var rejected = (await writer.SendAsync([WriteClient.Write(links, Content(new { entityId = target.EntityId.Value }))])).GetProperty("results")[0];
        await Assert.That(rejected.GetProperty("error").GetProperty("rule").GetString()).IsEqualTo("ENTITY-6");
        var ordered = await writer.SendAsync([
            WriteClient.Write(schema, new { name = "recreated" }, "target"),
            WriteClient.Write(links, Content(new { schemaId = schema.Value, key = new { scope = (string?)null, scheme = "test", value = "target" } }))]);
        await Assert.That(ordered.GetProperty("results")[1].TryGetProperty("version", out _)).IsTrue();
    }

    [Test, Property("caseId", "api-reading"), Property("caseId", "api-deletion")]
    public async Task DeletionRemovesRecognitionAndBatchReadPreservesOrderDuplicatesAndStatus()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var factory = new OrisumiApiFactory(db.ConnectionString);
        using var http = factory.CreateClient();
        var writer = new WriteClient(http); await writer.InitializeAsync();
        var schema = await writer.DeclareAsync();
        var first = await writer.WriteAsync(schema, new { }, "key");
        using var deleted = await http.DeleteAsync($"/entities/{first.EntityId.Value}"); deleted.EnsureSuccessStatusCode();
        var error = (await writer.SendAsync([WriteClient.Write(schema, new { }, entityId: first.EntityId)])).GetProperty("results")[0].GetProperty("error");
        await Assert.That(error.GetProperty("status").GetInt32()).IsEqualTo(409);
        await Assert.That(error.GetProperty("rule").GetString()).IsEqualTo("HISTORY-7");
        var recreated = await writer.WriteAsync(schema, new { }, "key");
        await Assert.That(recreated.EntityId != first.EntityId).IsTrue();
        using var read = await http.PostAsJsonAsync("/versions/read", new { versionIds = new[] { recreated.VersionId.Value, Guid.CreateVersion7(), first.VersionId.Value, recreated.VersionId.Value } });
        var results = (await WriteClient.JsonAsync(read)).GetProperty("results").EnumerateArray().ToArray();
        await Assert.That(results.Select(r => r.GetProperty("status").GetString()).SequenceEqual(["found", "not_found", "deleted", "found"])).IsTrue();
        using var excessive = await http.PostAsJsonAsync("/versions/read", new { versionIds = Enumerable.Repeat(first.VersionId.Value, 257) });
        await Assert.That(excessive.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        using var old = await http.PostAsJsonAsync("/versions", new { versions = Array.Empty<object>() });
        await Assert.That(old.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }
    [Test, Property("caseId", "api-lists")]
    public async Task ObservationPaginationBindsFiltersAndKeepsUnknownTimesLast()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var factory = new OrisumiApiFactory(db.ConnectionString);
        using var http = factory.CreateClient();
        var writer = new WriteClient(http); await writer.InitializeAsync();
        var schema = await writer.DeclareAsync();
        var results = (await writer.SendAsync([
            WriteClient.Write(schema, new { }, time: UnknownTime),
            WriteClient.Write(schema, new { }, time: new { startAt = "2026-10-31T01:00:00Z", endAt = (string?)null, timeZone = (string?)null }),
            WriteClient.Write(schema, new { }, time: new { startAt = (string?)null, endAt = "2026-10-31T02:00:00Z", timeZone = (string?)null })])).GetProperty("results");
        var observations = results.EnumerateArray().Select(r => r.GetProperty("observation").GetProperty("entityId").GetGuid()).ToArray();
        foreach (var order in new[] { "asc", "desc" })
        {
            var actual = new List<Guid>(); string? cursor = null;
            do
            {
                using var response = await http.GetAsync($"/observations?order={order}&limit=1&observerEntityId={writer.Observer.EntityId.Value}" + (cursor is null ? "" : "&after=" + Uri.EscapeDataString(cursor)));
                response.EnsureSuccessStatusCode(); var page = await WriteClient.JsonAsync(response);
                actual.AddRange(page.GetProperty("versions").EnumerateArray().Select(v => v.GetProperty("entityId").GetGuid()));
                cursor = page.GetProperty("next").GetString();
                if (cursor is not null)
                {
                    using var changed = await http.GetAsync("/observations?after=" + Uri.EscapeDataString(cursor));
                    await Assert.That(changed.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
                }
            } while (cursor is not null);
            await Assert.That(actual.SequenceEqual(order == "asc" ? [observations[1], observations[2], observations[0]] : new[] { observations[2], observations[1], observations[0] })).IsTrue();
        }
        using var half = await http.GetAsync("/observations?from=2026-10-31T00:00:00Z");
        await Assert.That(half.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        using var range = await http.GetAsync("/observations?from=2026-10-31T01:00:00Z&to=2026-10-31T02:00:00Z");
        await Assert.That((await WriteClient.JsonAsync(range)).GetProperty("versions").GetArrayLength()).IsEqualTo(1);
    }

    [Test, Property("caseId", "api-writes"), Property("caseId", "api-referrers")]
    public async Task InputsShareOneSetAndContentAndSchemaFailuresHaveDifferentBoundaries()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var factory = new OrisumiApiFactory(db.ConnectionString);
        using var http = factory.CreateClient();
        var writer = new WriteClient(http); await writer.InitializeAsync();
        var schema = await writer.DeclareAsync(fields: NameFields);
        var evidence = await writer.WriteAsync(schema, new { name = "evidence" });
        var results = (await writer.SendAsync([
            WriteClient.Write(schema, new { name = "one" }, time: UnknownTime, inputs: [writer.Observer.VersionId, evidence.VersionId]),
            WriteClient.Write(schema, new { name = "two" }, time: UnknownTime, inputs: [evidence.VersionId, writer.Observer.VersionId])])).GetProperty("results");
        var inputId = results[0].GetProperty("observation").GetProperty("content").GetProperty("inputSetVersionId").GetGuid();
        await Assert.That(results[1].GetProperty("observation").GetProperty("content").GetProperty("inputSetVersionId").GetGuid()).IsEqualTo(inputId);
        using var referrers = await http.GetAsync($"/entities/{evidence.EntityId.Value}/referrers?path=/content/members/1");
        var references = (await WriteClient.JsonAsync(referrers)).GetProperty("referrers");
        await Assert.That(references.GetArrayLength()).IsEqualTo(1);
        await Assert.That(references[0].GetProperty("version").GetProperty("versionId").GetGuid()).IsEqualTo(inputId);
        var undeclared = Guid.CreateVersion7();
        using var invalidSchema = await http.PostAsJsonAsync("/writes", new { observer = writer.ObserverReference, sequence = 20,
            schemas = new[] { new { schemaId = undeclared, content = new { displayName = "", description = "Invalid", resourceName = "invalid", fields = new { } } } },
            writes = new[] { WriteClient.Write(schema, new { name = "not saved" }) } });
        await Assert.That(invalidSchema.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        var valid = await writer.SendAsync([WriteClient.Write(schema, new { name = "saved" })], sequence: 20);
        await Assert.That(valid.GetProperty("results").GetArrayLength()).IsEqualTo(1);
        using var malformed = new StringContent("{\"observer\":" + JsonSerializer.Serialize(writer.ObserverReference) + ",\"writes\":[{\"schemaId\":\"" + schema.Value + "\",\"content\":{\"name\":\"first\",\"name\":\"second\"}}]}", System.Text.Encoding.UTF8, "application/json");
        using var duplicateMember = await http.PostAsync("/writes", malformed);
        await Assert.That(duplicateMember.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test, Property("caseId", "api-reading"), Property("caseId", "api-errors"), Property("caseId", "api-writes"), Property("caseId", "storage-transactions")]
    public async Task ConcurrentRecognitionPreservesOneChainAndHttpBoundariesRemainExplicit()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var factory = new OrisumiApiFactory(db.ConnectionString);
        using var http = factory.CreateClient();
        var writer = new WriteClient(http); await writer.InitializeAsync();
        var schema = await writer.DeclareAsync(fields: NameFields);
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => writer.WriteAsync(schema, new { name = "one" }, "shared")));
        await Assert.That(concurrent.Select(v => v.VersionId).Distinct().Count()).IsEqualTo(1);
        var first = concurrent[0];
        var second = await writer.WriteAsync(schema, new { name = "two" }, "shared");
        var third = await writer.WriteAsync(schema, new { name = "three" }, "shared", time: new { startAt = "2026-10-06T09:00:00+08:00", endAt = "2026-10-06T01:00:01.1234567Z", timeZone = "Asia/Shanghai" });
        using var chainResponse = await http.GetAsync($"entities/{first.EntityId.Value}/versions");
        var chain = await WriteClient.JsonAsync(chainResponse);
        await Assert.That(chain.GetProperty("versions").EnumerateArray().Select(v => v.GetProperty("versionId").GetGuid()).SequenceEqual([first.VersionId.Value, second.VersionId.Value, third.VersionId.Value])).IsTrue();
        using var readResponse = await http.GetAsync($"entities/{first.EntityId.Value}");
        var read = await WriteClient.JsonAsync(readResponse);
        await Assert.That(read.EnumerateObject().Count()).IsEqualTo(7);
        await Assert.That(read.GetProperty("recordedAt").GetString()!.EndsWith('Z')).IsTrue();
        using var observations = await http.GetAsync($"observations?contentEntityId={first.EntityId.Value}");
        var time = (await WriteClient.JsonAsync(observations)).GetProperty("versions")[0].GetProperty("content");
        await Assert.That(time.GetProperty("startAt").GetString()).IsEqualTo("2026-10-06T01:00:00.000000Z");
        await Assert.That(time.GetProperty("endAt").GetString()).IsEqualTo("2026-10-06T01:00:01.123456Z");
        using var invalid = await http.PostAsJsonAsync("writes", new { observer = writer.ObserverReference, writes = Array.Empty<object>(), recordedAt = "invalid" });
        using var missing = await http.GetAsync("entities/" + Guid.CreateVersion7());
        using var usedSchema = await http.DeleteAsync("entities/" + schema.Value);
        foreach (var response in new[] { invalid, missing, usedSchema })
        {
            var problem = await WriteClient.JsonAsync(response);
            await Assert.That(response.Content.Headers.ContentType!.MediaType).IsEqualTo("application/problem+json");
            await Assert.That(problem.GetProperty("status").GetInt32()).IsEqualTo((int)response.StatusCode);
            await Assert.That(problem.GetProperty("title").GetString()).IsNotNull();
            await Assert.That(problem.GetProperty("detail").GetString()).IsNotNull();
        }
        await Assert.That(invalid.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(missing.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(usedSchema.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        using var largeBody = new StringContent(new string(' ', 10 * 1024 * 1024 + 1), System.Text.Encoding.UTF8, "application/json");
        using var oversized = await http.PostAsync("writes", largeBody);
        await Assert.That(oversized.StatusCode).IsEqualTo(HttpStatusCode.RequestEntityTooLarge);
    }

}
