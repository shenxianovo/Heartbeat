using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Heartbeat.Core;
using Heartbeat.Core.DTOs.Facts;
using Heartbeat.Server.Services;

namespace Heartbeat.Server.Tests.Services;

public sealed partial class FactHttpTests
{
    [Theory]
    [InlineData("fresh.desktop", "desktop-activity", 1)]
    [InlineData("system", "selected-page", 0)]
    public async Task UsageSelectsObservationMeaning_IndependentlyOfCollectorSource(string source, string aspect, int expected)
    {
        var batch = FactStoreTests.SegmentBatch();
        batch.Streams[0].Source = source;
        batch.Facts[0].Aspect = aspect;
        batch.Facts[0].ObserverId = batch.Streams[0].CollectorInstanceId;
        batch.Facts[0].Target = new FactTarget("device", "hardware");
        batch.Facts[0].Payload = JsonSerializer.SerializeToElement(new { activityKey = "chrome", appIdentityKey = "mac:com.google.chrome", title = "Work" });
        await using var app = CreateApplication();
        using var http = app.CreateClient();
        http.DefaultRequestHeaders.Add(HeartbeatProtocol.VersionHeader, HeartbeatProtocol.RequiredVersion);
        http.DefaultRequestHeaders.Add("X-Test-Owner", "owner");
        using var first = await http.PostAsJsonAsync("/api/v1/facts", batch);
        Assert.True(first.IsSuccessStatusCode, await first.Content.ReadAsStringAsync());
        using var replay = await http.PostAsJsonAsync("/api/v1/facts", batch);
        Assert.True(replay.IsSuccessStatusCode);
        await using var db = CreateDbContext();
        Assert.Equal(expected, (await new UsageService(db).GetUsageAsync("owner", null, null, null)).Count);
        batch.Facts[0].Aspect = "custom.changed";
        using var conflict = await http.PostAsJsonAsync("/api/v1/facts", batch);
        Assert.Equal(System.Net.HttpStatusCode.Conflict, conflict.StatusCode);
    }

    [Fact]
    public async Task ExplicitUnknownAspectPreservesOpaqueResult_DespiteRecognizedSource()
    {
        var batch = FactStoreTests.SegmentBatch();
        batch.Streams[0].Source = "system";
        batch.Facts[0].ObserverId = batch.Streams[0].CollectorInstanceId;
        batch.Facts[0].Target = new FactTarget("device", "hardware");
        batch.Facts[0].Payload = JsonSerializer.SerializeToElement(new { identityKey = "private", activityKey = "different-private-value", extra = new[] { 1, 2 } });
        var json = JsonSerializer.SerializeToNode(batch, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        json["facts"]![0]!["aspect"] = "custom.snapshot";
        await using var app = CreateApplication();
        using var http = app.CreateClient();
        http.DefaultRequestHeaders.Add(HeartbeatProtocol.VersionHeader, HeartbeatProtocol.RequiredVersion);
        http.DefaultRequestHeaders.Add("X-Test-Owner", "owner");
        using var response = await http.PostAsJsonAsync("/api/v1/facts", json);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        await using var db = CreateDbContext();
        var fact = Assert.Single(await new FactStore(db).ReadSegmentsAsync("owner", null, null, null));
        Assert.Equal("custom.snapshot", fact.Aspect);
        Assert.True(JsonElement.DeepEquals(batch.Facts[0].Payload!.Value, fact.Payload));
        Assert.Empty(await new UsageService(db).GetUsageAsync("owner", null, null, null));
        Assert.Empty(await new UsageService(db).GetSegmentsAsync("owner", null, "system", null, null, null));
    }
}
