using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Heartbeat.Core.DTOs.Facts;

namespace Heartbeat.Server.Tests.Services;

public sealed partial class FactHttpTests
{
    private const string AnalysisDay = "version=1&kind=day&localDate=2026-01-01&timeZone=Etc%2FUTC&start=2026-01-01T00:00:00Z&endExclusive=2026-01-02T00:00:00Z";

    [Theory]
    [InlineData(null)]
    [InlineData("new-desktop-source")]
    public async Task IndependentAnalysis_HttpQueriesUseAspectAndExactAttributionWithoutStreams(string? source)
    {
        await SeedIndependentOwner();
        await using var app = CreateApplication();
        using var http = IndependentClient(app);
        var desktop = RelatedIndependentFact("analysis-machine");
        desktop.Aspect = "desktop-activity";
        desktop.Source = source;
        desktop.Foi = desktop.Relations[0].Members[1].Object;
        desktop.Start = DateTimeOffset.Parse("2025-12-31T23:59:00Z");
        desktop.Result = JsonSerializer.SerializeToElement(new { activityKey = "work", title = "Native desktop" });
        await PostIndependent(http, desktop, HttpStatusCode.OK);
        var raw = Assert.Single((await http.GetFromJsonAsync<List<FactResponse>>("/api/v1/users/alice/facts/segments"))!);
        var deviceId = raw.DeviceId!.Value;
        foreach (var aspect in new[] { "selected-page", "account-location", "activity", "unknown-aspect" })
        {
            var fact = RelatedIndependentFact("analysis-machine");
            fact.Aspect = aspect;
            fact.Source = aspect == "unknown-aspect" ? "system" : null;
            if (aspect is "account-location" or "activity") { fact.Foi = new("account", "example", aspect); fact.Relations.Clear(); }
            fact.Result = JsonSerializer.SerializeToElement(new { activityKey = aspect, title = aspect, attributes = new { windowId = 7, url = "https://example.test/page" }, future = new[] { 1, 2 } });
            await PostIndependent(http, fact, HttpStatusCode.OK);
        }
        var segments = await http.GetFromJsonAsync<JsonElement>("/api/v1/users/alice/segments");
        Assert.Equal(3, segments.GetArrayLength());
        Assert.All(segments.EnumerateArray(), row =>
        {
            Assert.Equal(JsonValueKind.Null, row.GetProperty("source").ValueKind);
            Assert.Equal(JsonValueKind.Null, row.GetProperty("streamId").ValueKind);
            Assert.Equal(JsonValueKind.Null, row.GetProperty("origin").ValueKind);
        });
        Assert.Equal(0, (await http.GetFromJsonAsync<JsonElement>("/api/v1/users/alice/segments?source=system")).GetArrayLength());
        var usage = Assert.Single((await http.GetFromJsonAsync<JsonElement>($"/api/v1/users/alice/usage?deviceId={deviceId}&start=2026-01-01T00:00:00Z&end=2026-01-02T00:00:00Z")).EnumerateArray());
        Assert.Equal(desktop.Id, usage.GetProperty("id").GetGuid());
        Assert.Equal(180, usage.GetProperty("durationSeconds").GetInt32());
        var daily = await http.GetFromJsonAsync<JsonElement>("/api/v1/users/alice/reports/daily?" + AnalysisDay);
        Assert.Equal(120, Assert.Single(daily.GetProperty("apps").EnumerateArray()).GetProperty("durationSeconds").GetInt32());
        var weekly = await http.GetFromJsonAsync<JsonElement>("/api/v1/users/alice/reports/weekly?version=1&kind=week&localDate=2026-01-01&timeZone=Etc%2FUTC&start=2025-12-29T00:00:00Z&endExclusive=2026-01-05T00:00:00Z");
        Assert.Equal(180, Assert.Single(weekly.GetProperty("apps").EnumerateArray()).GetProperty("durationSeconds").GetInt32());
        var experience = await http.GetFromJsonAsync<JsonElement>("/api/v1/users/alice/experience?" + AnalysisDay);
        Assert.Equal(5, experience.GetProperty("items").GetArrayLength());
        var unknown = Assert.Single(experience.GetProperty("items").EnumerateArray(), row => row.GetProperty("aspect").GetString() == "unknown-aspect");
        Assert.Equal(2, unknown.GetProperty("payload").GetProperty("future").GetArrayLength());
        Assert.Single((await http.GetFromJsonAsync<JsonElement>($"/api/v1/users/alice/segments?deviceId={deviceId}")).EnumerateArray());

        foreach (var aspect in new[] { "input", "unknown-input" })
        {
            var input = IndependentFact("event");
            input.Foi = desktop.Foi;
            input.Aspect = aspect;
            input.Result = JsonSerializer.SerializeToElement(new { eventType = "keyDown", codeSet = "heartbeat-key-position-v1", code = 30 });
            await PostIndependent(http, input, HttpStatusCode.OK);
        }
        var counts = await http.GetFromJsonAsync<JsonElement>($"/api/v1/users/alice/input-events/counts?deviceId={deviceId}&start=2026-01-01T00:00:00Z&end=2026-01-02T00:00:00Z");
        Assert.Equal(1, counts.GetProperty("keyboardTotal").GetInt64());
        var keys = await http.GetFromJsonAsync<JsonElement>($"/api/v1/users/alice/input-events/key-frequency?deviceId={deviceId}");
        Assert.Equal(1, Assert.Single(keys.GetProperty("keys").EnumerateArray()).GetProperty("count").GetInt64());
        var outside = await http.GetFromJsonAsync<JsonElement>($"/api/v1/users/alice/input-events/counts?deviceId={deviceId}&end=2026-01-01T00:00:00Z");
        Assert.Equal(0, outside.GetProperty("keyboardTotal").GetInt64());
    }
    [Fact]
    public async Task IndependentAnalysis_ExperiencePaginatesOpaqueFactsWithoutLosingParallelWindows()
    {
        await SeedIndependentOwner();
        await using var app = CreateApplication();
        using var http = IndependentClient(app);
        var facts = Enumerable.Range(0, 503).Select(index =>
        {
            var fact = RelatedIndependentFact(index % 2 == 0 ? "first-machine" : "second-machine");
            fact.Aspect = "selected-page";
            fact.Result = JsonSerializer.SerializeToElement(new
            {
                activityKey = "same-page", title = "Independent window", attributes = new { windowId = index, url = "https://example.test/" }
            });
            return fact;
        }).ToList();
        // Include opaque non-object JSON at the real read boundary as well as client roundtrips.
        facts[0].Aspect = "unknown-json";
        facts[0].Result = JsonSerializer.SerializeToElement(new object[] { false, 0, "original" });
        using var posted = await http.PostAsJsonAsync("/api/v1/observations", new ObservationUploadRequest { Facts = facts });
        Assert.True(posted.IsSuccessStatusCode, await posted.Content.ReadAsStringAsync());
        var first = (await http.GetFromJsonAsync<Heartbeat.Server.Services.ExperiencePage>("/api/v1/users/alice/experience?" + AnalysisDay))!;
        Assert.Equal(500, first.Items.Count);
        Assert.NotNull(first.NextCursor);
        var second = (await http.GetFromJsonAsync<Heartbeat.Server.Services.ExperiencePage>(
            "/api/v1/users/alice/experience?" + AnalysisDay + "&after=" + first.NextCursor))!;
        Assert.Equal(3, second.Items.Count);
        Assert.Null(second.NextCursor);
        var all = first.Items.Concat(second.Items).ToArray();
        Assert.Equal(503, all.Select(f => f.Id).Distinct().Count());
        Assert.Single(all.Select(f => f.FoiId).Distinct());
        Assert.Equal(2, all.Select(f => f.DeviceId).Distinct().Count());
        Assert.All(all, row => { Assert.Null(row.StreamId); Assert.Null(row.FactId); Assert.Null(row.Source); });
        Assert.True(JsonElement.DeepEquals(facts[0].Result!.Value, Assert.Single(all, f => f.Id == facts[0].Id).Payload));
        var device = all[0].DeviceId;
        var forDevice = (await http.GetFromJsonAsync<List<FactResponse>>($"/api/v1/users/alice/facts/segments?deviceId={device}"))!;
        Assert.Equal(all.Count(f => f.DeviceId == device), forDevice.Count);
        Assert.All(forDevice, row => Assert.Equal(device, row.DeviceId));
    }

}
