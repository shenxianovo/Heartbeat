using System.Net;
using System.Text.Json;
using Orisumi.Core;
using Orisumi.Testing;

namespace Orisumi.Integration.Tests;

public sealed class ReferrerApiTests
{
    [Test, Property("caseId", "api-referrers")]
    public async Task ReferencesFilterBeforePagingKeepPathsAndSurviveTargetDeletion()
    {
        await using var database = await DatabaseFixture.CreateAsync();
        await using var factory = new OrisumiApiFactory(database.ConnectionString);
        using var http = factory.CreateClient();
        var writer = new WriteClient(http); await writer.InitializeAsync();
        var items = await writer.DeclareAsync(fields: WriteApiTests.NameFields);
        var schema = await writer.DeclareAsync("links", """
            {"a~/VersionId":{"type":"reference","target":"version","description":"Target","required":true,"nullable":false},
             "otherVersionId":{"type":"reference","target":"version","description":"Other","required":true,"nullable":false}}
            """);
        var target = await writer.WriteAsync(items, new { name = "target" });
        var other = await writer.WriteAsync(items, new { name = "other" });
        object Content(VersionId a, VersionId b) => new Dictionary<string, Guid> { ["a~/VersionId"] = a.Value, ["otherVersionId"] = b.Value };
        var first = await writer.WriteAsync(schema, Content(target.VersionId, target.VersionId), "link");
        var current = await writer.WriteAsync(schema, Content(other.VersionId, target.VersionId), "link");
        var third = await writer.WriteAsync(schema, Content(target.VersionId, other.VersionId));
        var url = $"/entities/{target.EntityId.Value}/referrers";
        using var all = await http.GetAsync(url + "?versions=all");
        var entries = (await WriteClient.JsonAsync(all)).GetProperty("referrers");
        await Assert.That(entries.GetArrayLength()).IsEqualTo(3);
        await Assert.That(entries[0].GetProperty("references").GetArrayLength()).IsEqualTo(2);
        using var filtered = await http.GetAsync(url + "?versions=all&resourceName=links&path=/content/a~0~1VersionId&limit=1");
        var page = await WriteClient.JsonAsync(filtered);
        await Assert.That(page.GetProperty("referrers")[0].GetProperty("version").GetProperty("versionId").GetGuid()).IsEqualTo(first.VersionId.Value);
        var cursor = Uri.EscapeDataString(page.GetProperty("next").GetString()!);
        using var next = await http.GetAsync(url + "?versions=all&resourceName=links&path=/content/a~0~1VersionId&limit=1&after=" + cursor);
        await Assert.That((await WriteClient.JsonAsync(next)).GetProperty("referrers")[0].GetProperty("version").GetProperty("versionId").GetGuid()).IsEqualTo(third.VersionId.Value);
        using var changedQuery = await http.GetAsync(url + "?versions=all&resourceName=links&after=" + cursor);
        await Assert.That(changedQuery.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        using var descending = await http.GetAsync(url + "?order=desc");
        await Assert.That((await WriteClient.JsonAsync(descending)).GetProperty("referrers").EnumerateArray().Select(e => e.GetProperty("version").GetProperty("versionId").GetGuid()).SequenceEqual([third.VersionId.Value, current.VersionId.Value])).IsTrue();
        using var deleted = await http.DeleteAsync($"/entities/{target.EntityId.Value}"); deleted.EnsureSuccessStatusCode();
        using var retained = await http.GetAsync(url + "?versions=all");
        await Assert.That(JsonElement.DeepEquals(entries, (await WriteClient.JsonAsync(retained)).GetProperty("referrers"))).IsTrue();
        using var removed = await http.DeleteAsync($"/entities/{third.EntityId.Value}"); removed.EnsureSuccessStatusCode();
        using var after = await http.GetAsync(url);
        await Assert.That((await WriteClient.JsonAsync(after)).GetProperty("referrers").GetArrayLength()).IsEqualTo(1);
        using var unknown = await http.GetAsync($"/entities/{EntityId.New().Value}/referrers");
        await Assert.That(unknown.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }
}
