using System.Net.Http.Json;
using System.Text.Json;
using Heartbeat.Core;
using Heartbeat.Core.DTOs.Facts;
using Heartbeat.Server.Entities;

namespace Heartbeat.Server.Tests.Services;

public sealed partial class FactHttpTests
{
    [Fact]
    public async Task UnknownHistoricalTarget_RemainsReadableWithoutSubjectAliasesOrInventedDevice()
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
        var batch = FactStoreTests.SegmentBatch("person");
        batch.Streams[0].Source = "custom.observation";
        using var uploaded = await http.PostAsJsonAsync("/api/v1/facts", batch);
        Assert.True(uploaded.IsSuccessStatusCode, await uploaded.Content.ReadAsStringAsync());
        var fact = Assert.Single((await http.GetFromJsonAsync<List<FactResponse>>("/api/v1/users/alice/facts/segments"))!);
        Assert.Null(fact.FoiId);
        Assert.Null(fact.ObserverId);
        Assert.Null(fact.DeviceId);
        Assert.Equal(batch.Facts[0].FactId, fact.FactId);
        var start = batch.Facts[0].Start!.Value;
        var day = new DateTimeOffset(start.UtcDateTime.Date, TimeSpan.Zero);
        var window = $"version=1&kind=day&localDate={day:yyyy-MM-dd}&timeZone=Etc%2FUTC&start={Uri.EscapeDataString(day.ToString("O"))}&endExclusive={Uri.EscapeDataString(day.AddDays(1).ToString("O"))}";
        var experience = await http.GetFromJsonAsync<JsonElement>("/api/v1/users/alice/experience?" + window);
        var activity = await http.GetFromJsonAsync<JsonElement>("/api/v1/users/alice/segments");
        foreach (var row in new[] { Assert.Single(experience.GetProperty("items").EnumerateArray()), Assert.Single(activity.EnumerateArray()) })
        {
            Assert.False(row.TryGetProperty("subjectId", out _));
            Assert.False(row.TryGetProperty("subjectKind", out _));
            Assert.False(row.TryGetProperty("subjectName", out _));
            Assert.Equal(fact.FactId, row.GetProperty("factId").GetGuid());
        }
    }
}
