using System.Text.Json;
using Heartbeat.Collector.VRChat;
using Heartbeat.Hub.Runtime;
using Heartbeat.Management;

namespace Heartbeat.Hub.Tests;

public sealed class VRChatCollectorTests
{
    private const string Account = "usr_11111111-1111-4111-8111-111111111111";

    [Fact]
    public async Task LoginRequiresValidTwoFactorThenAutomaticallyCollectsAndRestoresWithoutCredentials()
    {
        using var host = new TestHub();
        var storage = host.Storage;
        var queue = host.Queue;
        var api = new FakeApi();
        var factory = new TestFactory(api, queue, storage.Secrets);
        await using (var manager = new CollectorManager(storage, [factory]))
        {
            var pending = await manager.LoginAsync(new(VRChatCollectorFactory.Key,
                JsonSerializer.SerializeToElement(new { username = "user", password = "private-password" })), TestContext.Current.CancellationToken);
            Assert.Null(pending.Target);
            Assert.Empty(manager.Collectors);
            Assert.Empty(queue.TakePending());
            var rejected = await manager.LoginAsync(new(VRChatCollectorFactory.Key,
                JsonSerializer.SerializeToElement(new { code = "wrong" }), SessionId: pending.SessionId), TestContext.Current.CancellationToken);
            Assert.NotNull(rejected.Error);
            Assert.Empty(queue.TakePending());
            var completed = await manager.LoginAsync(new(VRChatCollectorFactory.Key,
                JsonSerializer.SerializeToElement(new { code = "123456" }), SessionId: pending.SessionId), TestContext.Current.CancellationToken);
            Assert.Equal(Account, completed.Target);
            Assert.Equal("running", Assert.Single(manager.Collectors).State);
            var record = Assert.Single(queue.TakePending());
            Assert.Equal(Account, record.Route.Collector.Target);
            Assert.Equal("wrld_test", record.Record.Value.GetProperty("world_id").GetString());
            Assert.Equal("session", storage.Secrets.Read($"vrchat:{Account}:session"));
            var saved = File.ReadAllText(Path.Combine(host.Directory.FullName, "collectors.json"));
            Assert.DoesNotContain("private-password", saved);
            Assert.DoesNotContain("session", saved);
        }
        await using var restored = new CollectorManager(storage, [factory]);
        await restored.RestoreAsync(TestContext.Current.CancellationToken);
        Assert.Equal("running", Assert.Single(restored.Collectors).State);
        Assert.Equal(2, queue.TakePending().Count);
    }

    [Fact]
    public async Task ExpiredSessionCanReauthenticateButCannotChangeTheCollectorsAccount()
    {
        using var host = new TestHub();
        var storage = host.Storage;
        var queue = host.Queue;
        var api = new FakeApi { Verified = true, Expired = true };
        storage.WriteDocument("collectors", new[] { new { Key = VRChatCollectorFactory.Key, Target = Account } });
        storage.Secrets.Write($"vrchat:{Account}:session", "expired-session");
        await using var manager = new CollectorManager(storage, [new TestFactory(api, queue, storage.Secrets)]);
        await manager.RestoreAsync(TestContext.Current.CancellationToken);
        Assert.Equal("authentication_required", Assert.Single(manager.Collectors).State);
        Assert.Empty(queue.TakePending());
        api.Expired = false;
        api.AccountId = "usr_22222222-2222-4222-8222-222222222222";
        var login = new CollectorLoginRequest(VRChatCollectorFactory.Key,
            JsonSerializer.SerializeToElement(new { username = "user", password = "private-password" }), Account);
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.LoginAsync(login, TestContext.Current.CancellationToken));
        Assert.Equal("expired-session", storage.Secrets.Read($"vrchat:{Account}:session"));
        Assert.Empty(queue.TakePending());
        api.AccountId = Account;
        await manager.LoginAsync(login, TestContext.Current.CancellationToken);
        Assert.Equal("running", Assert.Single(manager.Collectors).State);
        Assert.Equal(Account, Assert.Single(queue.TakePending()).Route.Collector.Target);
    }

    private sealed class TestHub : IDisposable
    {
        public DirectoryInfo Directory { get; } = System.IO.Directory.CreateTempSubdirectory();
        public HubLocalStorage Storage { get; }
        public RecordOutbox Queue { get; }
        public TestHub()
        {
            Storage = new HubLocalStorage(Directory.FullName);
            Queue = new RecordOutbox(Storage.DatabasePath,
                new DeliveryDestination(new Uri("https://backend.example"), Guid.NewGuid()));
        }
        public void Dispose() { Storage.Dispose(); Directory.Delete(true); }
    }

    private sealed class TestFactory(FakeApi api, RecordOutbox queue, LocalSecretStore secrets) : ICollectorFactory
    {
        public CollectorType Type => new VRChatCollectorFactory(new LocalHubSubmissionClient(queue), secrets, "test").Type;
        public ICollectorSession Create(string? target) => new VRChatCollector(target, api, new LocalHubSubmissionClient(queue), secrets);
    }

    private sealed class FakeApi : IVRChatApiFactory, IVRChatApiSession
    {
        public bool Verified { get; set; }
        public bool Expired { get; set; }
        public string AccountId { get; set; } = Account;
        public IVRChatApiSession FromCredentials(string username, string password) => this;
        public IVRChatApiSession FromSession(string serializedSession) => this;
        public Task<VRChatAuthenticationState> AuthenticateAsync(CancellationToken cancellationToken) =>
            Expired ? throw new VRChatUnauthorizedException("expired") :
            Task.FromResult(new VRChatAuthenticationState("Example", Verified ? [] : ["emailOtp"], Verified ? AccountId : null));
        public Task VerifyTwoFactorAsync(string method, string code, CancellationToken cancellationToken)
        {
            Assert.Equal("emailOtp", method);
            if (code != "123456") throw new VRChatUnauthorizedException("wrong code");
            Verified = true;
            return Task.CompletedTask;
        }
        public Task<VRChatPresenceSnapshot> GetSnapshotAsync(CancellationToken token) =>
            Task.FromResult(new VRChatPresenceSnapshot(DateTimeOffset.UtcNow,
                [new(AccountId, "Example", new("wrld_test", "instance"), DateTimeOffset.UtcNow, "snapshot")]));
        public Task<IVRChatEventConnection> ConnectAsync(CancellationToken token) =>
            Task.FromResult<IVRChatEventConnection>(new Heartbeat.Testing.IdleVRChatConnection());
        public Task<string?> GetWorldNameAsync(string worldId, CancellationToken cancellationToken) => Task.FromResult<string?>("World");
        public string ExportSession() => "session";
    }
}
