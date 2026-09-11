using System.Net.Http.Json;
using System.Text.Json;
using Heartbeat.Core;
using Heartbeat.Core.DTOs.Facts;

namespace Heartbeat.Server.Tests.Services;

public sealed partial class FactHttpTests
{
    [Fact]
    public async Task NativeObjectRelationsRoundTripWithoutTarget_AndConflictingReplayIsRejected()
    {
        await using (var db = CreateDbContext())
        {
            db.Users.Add(new Heartbeat.Server.Entities.User { Id = "owner", Username = "alice" });
            await db.SaveChangesAsync();
        }
        var batch = FactStoreTests.SegmentBatch();
        var fact = batch.Facts[0];
        fact.CollectorId = batch.Streams[0].CollectorInstanceId;
        fact.Foi = new("app", ObservationObjectScopes.App, "chrome");
        fact.Aspect = "selected-page";
        fact.Relations = [new("observed-on", [
            new("app", fact.Foi),
            new("device", new("machine", ObservationObjectScopes.Machine, "mac-one"))])];
        fact.Payload = JsonSerializer.SerializeToElement(new { activityKey = "https://example.com", title = "Page" });
        await using var app = CreateApplication();
        using var http = app.CreateClient();
        http.DefaultRequestHeaders.Add(HeartbeatProtocol.VersionHeader, HeartbeatProtocol.RequiredVersion);
        http.DefaultRequestHeaders.Add("X-Test-Owner", "owner");
        using var saved = await http.PostAsJsonAsync("/api/v1/facts", batch);
        Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync());
        var rows = await http.GetFromJsonAsync<JsonElement>("/api/v1/users/alice/facts/segments");
        var row = Assert.Single(rows.EnumerateArray());
        Assert.Equal("app", row.GetProperty("foi").GetProperty("kind").GetString());
        Assert.Equal("chrome", row.GetProperty("foi").GetProperty("key").GetString());
        Assert.Equal(fact.CollectorId, row.GetProperty("collectorId").GetGuid());
        var relation = Assert.Single(row.GetProperty("relations").EnumerateArray());
        Assert.Equal("observed-on", relation.GetProperty("kind").GetString());
        Assert.Equal(2, relation.GetProperty("members").GetArrayLength());
        using var replay = await http.PostAsJsonAsync("/api/v1/facts", batch);
        Assert.True(replay.IsSuccessStatusCode, await replay.Content.ReadAsStringAsync());
        fact.Relations[0].Members[1] = new("device", new("machine", ObservationObjectScopes.Machine, "windows-two"));
        using var conflict = await http.PostAsJsonAsync("/api/v1/facts", batch);
        Assert.Equal(System.Net.HttpStatusCode.Conflict, conflict.StatusCode);
        fact.Revision++;
        fact.End = fact.Start!.Value.AddSeconds(5);
        // Conflicting devices across relation kinds must not create ambiguous attribution.
        fact.Relations.Add(new("installed-on", [new("app", fact.Foi),
            new("device", new("machine", ObservationObjectScopes.Machine, "mac-one"))]));
        using var ambiguous = await http.PostAsJsonAsync("/api/v1/facts", batch);
        Assert.Equal(System.Net.HttpStatusCode.UnprocessableEntity, ambiguous.StatusCode);
        fact.Relations.RemoveAt(1);
        using var revised = await http.PostAsJsonAsync("/api/v1/facts", batch);
        Assert.True(revised.IsSuccessStatusCode, await revised.Content.ReadAsStringAsync());
        var after = Assert.Single((await http.GetFromJsonAsync<JsonElement>("/api/v1/users/alice/facts/segments")).EnumerateArray());
        Assert.Equal(row.GetProperty("id").GetGuid(), after.GetProperty("id").GetGuid());
        var newRelation = Assert.Single(after.GetProperty("relations").EnumerateArray());
        Assert.Equal(relation.GetProperty("id").GetGuid(), newRelation.GetProperty("id").GetGuid());
        Assert.Equal(fact.End, newRelation.GetProperty("validTo").GetDateTimeOffset());
        Assert.Equal("windows-two", newRelation.GetProperty("members").EnumerateArray()
            .Single(m => m.GetProperty("role").GetString() == "device").GetProperty("object").GetProperty("key").GetString());
        Assert.Empty((await http.GetFromJsonAsync<JsonElement>($"/api/v1/users/alice/facts/segments?deviceId={row.GetProperty("deviceId").GetInt64()}")).EnumerateArray());
    }
}
