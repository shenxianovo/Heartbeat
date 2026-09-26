using System.Net.Http.Json;
using System.Text.Json;
using Heartbeat.Collector.VRChat;
using Heartbeat.Hub;
using Heartbeat.Hub.Runtime;
using Heartbeat.Management;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Integration.Tests;

[Collection(PostgresTestGroup.Name)]
public sealed class ManagedVRChatTests(PostgresFixture fixture) : PostgresTestBase(fixture)
{
    private const string Account = "usr_11111111-1111-4111-8111-111111111111";

    [Fact]
    public async Task OnlineConfigurationAndStartProduceReplayableRecordsWithoutPersistingCredentialsInApi()
    {
        var owner = Guid.NewGuid();
        await using var api = RecordingApiFactory.Create(ConnectionString, TimeProvider.System);
        using var http = api.CreateClient();
        http.DefaultRequestHeaders.Add(RecordingApiFactory.OwnerHeader, owner.ToString());
        var directory = Directory.CreateTempSubdirectory();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        stop.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            using var storage = new HubLocalStorage(directory.FullName);
            var destination = new DeliveryDestination(http.BaseAddress!, owner);
            var queue = new RecordOutbox(Path.Combine(directory.FullName, "hub.sqlite"), destination);
            var secrets = storage.Secrets;
            var factory = new TestFactory(new LocalHubSubmissionClient(queue), secrets);
            await using var manager = new CollectorManager(storage, [factory]);
            var id = storage.Id;
            var tokens = new Tokens(owner);
            var loop = new HubManagementLoop(http, tokens, destination, id,
                () => new("Test server", "server", manager.Types, manager.Collectors,
                    new(queue.Status().Pending, queue.Status().Failed, null)), manager, queue.Activity);
            var running = loop.RunAsync(stop.Token);
            try
            {
                while (!(await http.GetFromJsonAsync<HubList>("/api/v1/hubs", stop.Token))!.Hubs.Any(x => x.Id == id))
                    await Task.Delay(10, stop.Token);
                await OperateAsync(new("configure", VRChatCollectorFactory.Key, Account,
                    JsonSerializer.SerializeToElement(new { username = "user", password = "private-password" })));
                await OperateAsync(new("start", VRChatCollectorFactory.Key, Account));
                Assert.Single(queue.TakePending());
                Assert.Empty(await new RecordUploader(queue, http, tokens).UploadOnceAsync(cancellationToken: stop.Token));
                await OperateAsync(new("pause", VRChatCollectorFactory.Key, Account));
                Assert.Equal("paused", Assert.Single(manager.Collectors).State);
                while ((await http.GetFromJsonAsync<ActivityList>("/api/v1/hubs/activity", stop.Token))!
                    .Activities.GetValueOrDefault(id)?.Buckets.Sum(bucket => bucket.Confirmed) != 1)
                    await Task.Delay(50, stop.Token);
                var activity = (await http.GetFromJsonAsync<ActivityList>("/api/v1/hubs/activity", stop.Token))!.Activities[id];
                Assert.Equal(1, activity.Buckets.Sum(bucket => bucket.Received));
                Assert.Equal(1, activity.Buckets.Sum(bucket => bucket.Sent));
                await using var db = CreateDbContext();
                var stored = await db.Records.SingleAsync(stop.Token);
                using var replay = await http.GetAsync($"/api/v1/tracks/{stored.TrackId}/records", stop.Token);
                replay.EnsureSuccessStatusCode();
                Assert.Contains("world_id", await replay.Content.ReadAsStringAsync(stop.Token));
                var report = await db.Hubs.SingleAsync(stop.Token);
                Assert.DoesNotContain("private-password", report.StatusJson);
                Assert.DoesNotContain("session-cookie", report.StatusJson);
                Assert.DoesNotContain("private-password", File.ReadAllText(Path.Combine(directory.FullName, "collectors.json")));
            }
            finally
            {
                await stop.CancelAsync();
                try { await running; } catch (OperationCanceledException) { }
            }

            async Task OperateAsync(CollectorOperation operation)
            {
                using var response = await http.PostAsJsonAsync($"/api/v1/hubs/{id}/operations", operation, stop.Token);
                response.EnsureSuccessStatusCode();
                var result = await response.Content.ReadFromJsonAsync<HubCommandResult>(stop.Token);
                Assert.True(result!.Succeeded, result.Error);
            }
        }
        finally { directory.Delete(true); }
    }

    private sealed record ActivityList(Dictionary<Guid, DeliveryActivitySnapshot> Activities);
    private sealed record HubList(HubSummary[] Hubs);
    private sealed class Tokens(Guid owner) : IBackendTokenProvider
    {
        public ValueTask<BackendAccessToken?> GetTokenAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<BackendAccessToken?>(new("test-token", owner, DateTimeOffset.UtcNow.AddHours(1)));
        public void Invalidate() { }
    }
    private sealed class TestFactory(IHubSubmissionClient hub, LocalSecretStore secrets) : ICollectorFactory
    {
        public CollectorType Type => new VRChatCollectorFactory(hub, secrets, "test").Type;
        public IManagedCollector Create(string target) => new VRChatCollector(target, new FakeApi(), hub, secrets);
    }
    private sealed class FakeApi : IVRChatApiFactory, IVRChatApiSession
    {
        public IVRChatApiSession FromCredentials(string username, string password) => this;
        public IVRChatApiSession FromSession(string serializedSession) => this;
        public Task<VRChatAuthenticationState> AuthenticateAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new VRChatAuthenticationState("Example", [], Account));
        public Task VerifyTwoFactorAsync(string method, string code, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<VRChatPresence?> GetPresenceAsync(CancellationToken cancellationToken) =>
            Task.FromResult<VRChatPresence?>(new("world", "World", "instance", Account));
        public Task<string?> GetWorldNameAsync(string worldId, CancellationToken cancellationToken) => Task.FromResult<string?>("World");
        public string ExportSession() => "session-cookie";
    }
}
