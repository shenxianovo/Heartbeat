using System.Net;
using System.Net.Http.Json;
using Heartbeat.Core;
using Heartbeat.Core.DTOs.Facts;
using Heartbeat.Server.Entities;

namespace Heartbeat.Server.Tests.Services;

public sealed partial class FactHttpTests
{
    [Fact]
    public async Task ObservationIdentityIsReadableAfterHttpReplay_WithoutJoiningAnotherDevicesFacts()
    {
        await using (var db = CreateDbContext())
        {
            db.Users.Add(new User { Id = "owner", Username = "alice" });
            await db.SaveChangesAsync();
        }
        await using var app = CreateApplication();
        using var http = app.CreateClient();
        http.DefaultRequestHeaders.Add(HeartbeatProtocol.VersionHeader, HeartbeatProtocol.RequiredVersion);
        http.DefaultRequestHeaders.Add("X-Test-Owner", "owner");
        var mac = BrowserApplicationContextTests.Batch("mac:com.test.chrome", "mac");
        var windows = BrowserApplicationContextTests.Batch("mac:com.test.chrome", "windows");
        foreach (var batch in new[] { mac, windows, mac })
        {
            using var response = await http.PostAsJsonAsync("/api/v1/facts", batch);
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        }
        var facts = (await http.GetFromJsonAsync<List<FactResponse>>("/api/v1/users/alice/facts/segments"))!;
        Assert.Equal(2, facts.Count);
        Assert.NotNull(facts[0].FoiId);
        Assert.Equal(facts[0].FoiId, facts[1].FoiId);
        Assert.All(facts, f => Assert.Equal("selected-page", f.Aspect));
        Assert.NotEqual(facts[0].DeviceId, facts[1].DeviceId);
        var selected = Assert.Single((await http.GetFromJsonAsync<List<FactResponse>>($"/api/v1/users/alice/facts/segments?deviceId={facts[0].DeviceId}"))!);
        Assert.Equal(facts[0].Id, selected.Id);
        http.DefaultRequestHeaders.Remove("X-Test-Owner");
        using var hidden = await http.GetAsync("/api/v1/users/alice/facts/segments");
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
    }
}
