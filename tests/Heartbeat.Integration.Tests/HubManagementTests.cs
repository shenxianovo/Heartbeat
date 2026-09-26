using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Heartbeat.Management;

namespace Heartbeat.Integration.Tests;

[Collection(PostgresTestGroup.Name)]
public sealed class HubManagementTests(PostgresFixture fixture) : PostgresTestBase(fixture)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly HubReport Report = new("Server", "server", [new("example", "Example", [])], [], new(7, 1, null));

    [Fact]
    public async Task OwnerScopedPresenceExpiresWithoutRecordsAndRetirementSurvivesReconnect()
    {
        var clock = new MutableClock();
        await using var factory = RecordingApiFactory.Create(ConnectionString, clock);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(RecordingApiFactory.OwnerHeader, Guid.NewGuid().ToString());
        var id = Guid.NewGuid();
        var checkIn = new HubCheckIn(Guid.NewGuid(), Report);
        using var first = await client.PostAsJsonAsync($"/api/v1/hubs/{id}/check-in", checkIn, Token);
        first.EnsureSuccessStatusCode();
        var hubs = await client.GetFromJsonAsync<HubList>("/api/v1/hubs", Token);
        Assert.True(Assert.Single(hubs!.Hubs).Online);
        Assert.Equal(7, hubs.Hubs[0].Report.Delivery.Pending);
        using var other = factory.CreateClient();
        other.DefaultRequestHeaders.Add(RecordingApiFactory.OwnerHeader, Guid.NewGuid().ToString());
        Assert.Empty((await other.GetFromJsonAsync<HubList>("/api/v1/hubs", Token))!.Hubs);
        using var rejected = await other.PostAsJsonAsync($"/api/v1/hubs/{id}/check-in", checkIn, Token);
        Assert.Equal(HttpStatusCode.NotFound, rejected.StatusCode);
        clock.Now += HubManagement.OnlineTimeout;
        Assert.False(Assert.Single((await client.GetFromJsonAsync<HubList>("/api/v1/hubs", Token))!.Hubs).Online);
        using var retired = await client.PostAsJsonAsync($"/api/v1/hubs/{id}/retire", new { }, Token);
        Assert.Equal(HttpStatusCode.NoContent, retired.StatusCode);
        using var reconnect = await client.PostAsJsonAsync($"/api/v1/hubs/{id}/check-in", checkIn, Token);
        Assert.Equal(HttpStatusCode.NotFound, reconnect.StatusCode);
    }

    [Fact]
    public async Task OnlineOperationIsDeliveredOnceAndOnlySucceedsAfterHubAcknowledgement()
    {
        await using var factory = RecordingApiFactory.Create(ConnectionString, TimeProvider.System);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(RecordingApiFactory.OwnerHeader, Guid.NewGuid().ToString());
        var id = Guid.NewGuid();
        var checkIn = new HubCheckIn(Guid.NewGuid(), Report);
        using var first = await client.PostAsJsonAsync($"/api/v1/hubs/{id}/check-in", checkIn, Token);
        first.EnsureSuccessStatusCode();
        var operation = client.PostAsJsonAsync($"/api/v1/hubs/{id}/operations", new CollectorOperation("start", "example", "target"), Token);
        HubCommand? command = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        while (command is null)
        {
            using var response = await client.PostAsJsonAsync($"/api/v1/hubs/{id}/check-in", checkIn, deadline.Token);
            response.EnsureSuccessStatusCode();
            command = (await response.Content.ReadFromJsonAsync<HubCheckInResponse>(deadline.Token))!.Command;
            if (command is null) await Task.Delay(10, deadline.Token);
        }
        Assert.False(operation.IsCompleted);
        using var busy = await client.PostAsJsonAsync($"/api/v1/hubs/{id}/operations",
            new CollectorOperation("configure", "example", "unclaimed"), Token);
        Assert.Equal(HttpStatusCode.Conflict, busy.StatusCode);
        using var unclaimed = await client.PostAsJsonAsync($"/api/v1/hubs/{Guid.NewGuid()}/check-in",
            new HubCheckIn(Guid.NewGuid(), Report with { Collectors = [new("example", "unclaimed", "Example", "paused", null)] }), Token);
        Assert.Equal(HttpStatusCode.OK, unclaimed.StatusCode);
        using var repeated = await client.PostAsJsonAsync($"/api/v1/hubs/{id}/check-in", checkIn, Token);
        Assert.Null((await repeated.Content.ReadFromJsonAsync<HubCheckInResponse>(Token))!.Command);
        using var acknowledgement = await client.PostAsJsonAsync($"/api/v1/hubs/{id}/check-in",
            checkIn with { Result = new(command.Id, true) }, Token);
        acknowledgement.EnsureSuccessStatusCode();
        using var completed = await operation;
        Assert.True((await completed.Content.ReadFromJsonAsync<HubCommandResult>(Token))!.Succeeded);
    }

    [Fact]
    public async Task DuplicateHubSessionCannotOverwriteTheAcceptedReport()
    {
        await using var factory = RecordingApiFactory.Create(ConnectionString, TimeProvider.System);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(RecordingApiFactory.OwnerHeader, Guid.NewGuid().ToString());
        var id = Guid.NewGuid();
        using var first = await client.PostAsJsonAsync($"/api/v1/hubs/{id}/check-in", new HubCheckIn(Guid.NewGuid(), Report), Token);
        first.EnsureSuccessStatusCode();
        using var duplicate = await client.PostAsJsonAsync($"/api/v1/hubs/{id}/check-in",
            new HubCheckIn(Guid.NewGuid(), Report with { DisplayName = "Clone" }), Token);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("Server", Assert.Single((await client.GetFromJsonAsync<HubList>("/api/v1/hubs", Token))!.Hubs).Report.DisplayName);
    }

    [Fact]
    public async Task SameCollectorCanBeReportedByTwoHubsWithoutServerAssignment()
    {
        await using var factory = RecordingApiFactory.Create(ConnectionString, TimeProvider.System);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(RecordingApiFactory.OwnerHeader, Guid.NewGuid().ToString());
        var report = Report with { Collectors = [new("example", "target", "Example", "paused", null)] };
        using var first = await client.PostAsJsonAsync($"/api/v1/hubs/{Guid.NewGuid()}/check-in", new HubCheckIn(Guid.NewGuid(), report), Token);
        first.EnsureSuccessStatusCode();
        using var second = await client.PostAsJsonAsync($"/api/v1/hubs/{Guid.NewGuid()}/check-in", new HubCheckIn(Guid.NewGuid(), report), Token);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(2, (await client.GetFromJsonAsync<HubList>("/api/v1/hubs", Token))!.Hubs.Length);
    }

    [Fact]
    public async Task OfflineStatusSurvivesApiRestartButPublicConfigurationDoesNotEnterDatabase()
    {
        var clock = new MutableClock();
        var owner = Guid.NewGuid();
        var id = Guid.NewGuid();
        var report = Report with { Collectors = [new("example", "target", "Example", "paused", null,
            JsonSerializer.SerializeToElement(new { localSetting = "only-on-executing-hub" }))] };
        await using (var factory = RecordingApiFactory.Create(ConnectionString, clock))
        {
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Add(RecordingApiFactory.OwnerHeader, owner.ToString());
            using var response = await client.PostAsJsonAsync($"/api/v1/hubs/{id}/check-in", new HubCheckIn(Guid.NewGuid(), report), Token);
            response.EnsureSuccessStatusCode();
            var live = Assert.Single((await client.GetFromJsonAsync<HubList>("/api/v1/hubs", Token))!.Hubs);
            Assert.Equal("only-on-executing-hub", live.Report.Collectors[0].Configuration!.Value.GetProperty("localSetting").GetString());
            await using var db = CreateDbContext();
            var saved = await db.Hubs.SingleAsync(Token);
            Assert.DoesNotContain("configuration", saved.StatusJson);
            Assert.DoesNotContain("only-on-executing-hub", saved.StatusJson);
            Assert.DoesNotContain("types", saved.StatusJson);
        }
        clock.Now += HubManagement.OnlineTimeout;
        await using var restarted = RecordingApiFactory.Create(ConnectionString, clock);
        using var offline = restarted.CreateClient();
        offline.DefaultRequestHeaders.Add(RecordingApiFactory.OwnerHeader, owner.ToString());
        var hub = Assert.Single((await offline.GetFromJsonAsync<HubList>("/api/v1/hubs", Token))!.Hubs);
        Assert.False(hub.Online);
        Assert.Equal("paused", Assert.Single(hub.Report.Collectors).State);
        Assert.Equal(7, hub.Report.Delivery.Pending);
        Assert.Null(hub.Report.Collectors[0].Configuration);
        Assert.Empty(hub.Report.Types);
    }

    [Fact]
    public async Task ActivityRequiresLiveOwnerSessionAndNeverRefreshesPresenceOrEntersStorage()
    {
        var id = Guid.NewGuid();
        var checkIn = new HubCheckIn(Guid.NewGuid(), Report);
        var clock = new MutableClock();
        await using var factory = RecordingApiFactory.Create(ConnectionString, clock);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(RecordingApiFactory.OwnerHeader, Guid.NewGuid().ToString());
        using var registered = await client.PostAsJsonAsync($"/api/v1/hubs/{id}/check-in", checkIn, Token);
        registered.EnsureSuccessStatusCode();
        var activity = new HubActivityReport(checkIn.SessionId, new(Guid.NewGuid(), 60_000, [new(59, 10, 8, 0), new(60, 0, 0, 3)]));
        using var accepted = await client.PostAsJsonAsync($"/api/v1/hubs/{id}/activity", activity, Token);
        Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
        Assert.Equal(activity.Activity.Buckets, Assert.Single((await client.GetFromJsonAsync<ActivityList>("/api/v1/hubs/activity", Token))!.Activities).Value.Buckets);
        using var other = factory.CreateClient();
        other.DefaultRequestHeaders.Add(RecordingApiFactory.OwnerHeader, Guid.NewGuid().ToString());
        Assert.Empty((await other.GetFromJsonAsync<ActivityList>("/api/v1/hubs/activity", Token))!.Activities);
        using var foreign = await other.PostAsJsonAsync($"/api/v1/hubs/{id}/activity", activity, Token);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        using var clone = await client.PostAsJsonAsync($"/api/v1/hubs/{id}/activity", activity with { SessionId = Guid.NewGuid() }, Token);
        Assert.Equal(HttpStatusCode.NotFound, clone.StatusCode);
        using var late = await client.PostAsJsonAsync($"/api/v1/hubs/{id}/activity",
            activity with { Activity = activity.Activity with { CapturedAt = 59_000, Buckets = [new(59, 1, 1, 0)] } }, Token);
        Assert.Equal(HttpStatusCode.NotFound, late.StatusCode);
        await using var db = CreateDbContext();
        Assert.DoesNotContain(activity.Activity.Epoch.ToString(), (await db.Hubs.SingleAsync(Token)).StatusJson);
        clock.Now += HubManagement.ActivityTimeout;
        Assert.Empty((await client.GetFromJsonAsync<ActivityList>("/api/v1/hubs/activity", Token))!.Activities);
        clock.Now += HubManagement.OnlineTimeout;
        using var expired = await client.PostAsJsonAsync($"/api/v1/hubs/{id}/activity", activity, Token);
        Assert.Equal(HttpStatusCode.NotFound, expired.StatusCode);
        Assert.False(Assert.Single((await client.GetFromJsonAsync<HubList>("/api/v1/hubs", Token))!.Hubs).Online);
    }

    private sealed record ActivityList(Dictionary<Guid, DeliveryActivitySnapshot> Activities);

    private sealed record HubList(HubSummary[] Hubs);
    private sealed class MutableClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
