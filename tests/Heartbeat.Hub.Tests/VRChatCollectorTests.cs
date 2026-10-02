using System.Text.Json;
using Heartbeat.Collector.VRChat;
using Heartbeat.Hub.Runtime;
using Heartbeat.Management;

namespace Heartbeat.Hub.Tests;

public sealed class VRChatCollectorTests
{
    private const string Account = "usr_11111111-1111-4111-8111-111111111111";

    [Fact]
    public async Task RealSdkRestoresExactCookiesBeforeAnyRestRequestOrPipelineConnection()
    {
        using var factory = new VRChatApiFactory("Heartbeat", "test", "test@example.com");
        CookieRecord[] cookies = [new("auth", "authcookie_test"), new("twoFactorAuth", "twofactorauth_test")];
        var session = factory.FromSession(JsonSerializer.Serialize(cookies));
        Assert.Equal(cookies, JsonSerializer.Deserialize<CookieRecord[]>(session.ExportSession()));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.ConnectAsync(cancelled.Token));
    }

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
            Assert.Equal("wrld_test", record.Record.Objects.Single(reference => reference.Role == "world").Key);
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

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task RejectedCollectionKeepsAValidSessionRecoverable(int status)
    {
        using var host = new TestHub();
        var api = new FakeApi { Verified = true, ConnectFailure = new VRChatUnauthorizedException($"VRChat 事件连接 HTTP {status}") };
        await using var manager = new CollectorManager(host.Storage, [new TestFactory(api, host.Queue, host.Storage.Secrets)]);
        await manager.LoginAsync(new(VRChatCollectorFactory.Key,
            JsonSerializer.SerializeToElement(new { username = "user", password = "password" })), TestContext.Current.CancellationToken);
        var state = Assert.Single(manager.Collectors);
        Assert.Equal("error", state.State);
        Assert.Contains($"HTTP {status}", state.Error);
        Assert.Contains("会话核验有效", state.Error);
        Assert.Empty(host.Queue.TakePending());
        Assert.Equal(2, api.AuthenticationCalls);
    }

    [Theory]
    [InlineData("rejected", "会话已失效")]
    [InlineData("twoFactor", "两步验证")]
    [InlineData("accountMismatch", "账号不匹配")]
    public async Task ConfirmedInvalidSessionRequiresLogin(string outcome, string reason)
    {
        using var host = new TestHub();
        var api = new FakeApi
        {
            Verified = true,
            ConnectFailure = new VRChatUnauthorizedException("VRChat 事件连接 HTTP 403"),
            Reauthentication = _ => outcome switch
            {
                "rejected" => throw new VRChatUnauthorizedException("VRChat 会话认证 HTTP 401"),
                "twoFactor" => Task.FromResult(new VRChatAuthenticationState(null, ["emailOtp"], null)),
                _ => Task.FromResult(new VRChatAuthenticationState("Other", [], "usr_22222222-2222-4222-8222-222222222222")),
            },
        };
        await using var manager = new CollectorManager(host.Storage, [new TestFactory(api, host.Queue, host.Storage.Secrets)]);
        await manager.LoginAsync(new(VRChatCollectorFactory.Key,
            JsonSerializer.SerializeToElement(new { username = "user", password = "password" })), TestContext.Current.CancellationToken);
        var state = Assert.Single(manager.Collectors);
        Assert.Equal("authentication_required", state.State);
        Assert.Contains(reason, state.Error);
        Assert.Empty(host.Queue.TakePending());
    }

    [Fact]
    public async Task TemporaryVerificationFailureIsRecheckedBeforeCollectionResumes()
    {
        using var host = new TestHub();
        var checks = 0;
        var api = new FakeApi
        {
            Verified = true,
            ConnectFailure = new VRChatUnauthorizedException("VRChat 事件连接 HTTP 403"),
            Reauthentication = _ => ++checks == 1
                ? throw new VRChatTransientException("VRChat 会话认证 HTTP 429", TimeSpan.FromSeconds(1))
                : Task.FromResult(new VRChatAuthenticationState("Example", [], Account)),
        };
        await using var manager = new CollectorManager(host.Storage, [new TestFactory(api, host.Queue, host.Storage.Secrets)]);
        await manager.LoginAsync(new(VRChatCollectorFactory.Key,
            JsonSerializer.SerializeToElement(new { username = "user", password = "password" })), TestContext.Current.CancellationToken);
        Assert.Equal("error", Assert.Single(manager.Collectors).State);
        Assert.Contains("会话核验暂时失败", Assert.Single(manager.Collectors).Error);
        Assert.Empty(host.Queue.TakePending());
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        while (host.Queue.TakePending().Count == 0) await Task.Delay(50, timeout.Token);
        Assert.Equal("running", Assert.Single(manager.Collectors).State);
        Assert.Equal(2, checks);
        Assert.Equal(2, api.ConnectAttempts);
        Assert.Equal(Account, Assert.Single(host.Queue.TakePending()).Route.Collector.Target);
    }

    [Fact]
    public async Task SnapshotAccountMismatchStopsWithoutTrustingAnOtherwiseValidSession()
    {
        using var host = new TestHub();
        var api = new FakeApi { Verified = true, SnapshotAccountId = "usr_22222222-2222-4222-8222-222222222222" };
        await using var manager = new CollectorManager(host.Storage, [new TestFactory(api, host.Queue, host.Storage.Secrets)]);
        await manager.LoginAsync(new(VRChatCollectorFactory.Key,
            JsonSerializer.SerializeToElement(new { username = "user", password = "password" })), TestContext.Current.CancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (Assert.Single(manager.Collectors).State == "running") await Task.Delay(10, timeout.Token);
        Assert.Equal("authentication_required", Assert.Single(manager.Collectors).State);
        Assert.Contains("账号不匹配", Assert.Single(manager.Collectors).Error);
        Assert.Equal(1, api.AuthenticationCalls);
        Assert.Empty(host.Queue.TakePending());
    }

    [Fact]
    public async Task StoppingDuringSessionVerificationDoesNotReportExpiration()
    {
        using var host = new TestHub();
        var checking = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = new FakeApi
        {
            Verified = true,
            ConnectFailure = new VRChatUnauthorizedException("VRChat 事件连接 HTTP 403"),
            Reauthentication = async token =>
            {
                checking.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException("Unreachable after cancellation.");
            },
        };
        await using var collector = new VRChatCollector(null, api, new LocalHubSubmissionClient(host.Queue), host.Storage.Secrets);
        await collector.LoginAsync(JsonSerializer.SerializeToElement(new { username = "user", password = "password" }), TestContext.Current.CancellationToken);
        await collector.StartAsync(TestContext.Current.CancellationToken);
        await checking.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await collector.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.NotEqual("authentication_required", collector.State!.State);
        Assert.Empty(host.Queue.TakePending());
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task RejectionDiagnosticsContainOnlyOperationAndHttpStatus(int status)
    {
        using var gate = new VRChatRequestGate();
        var exception = await Assert.ThrowsAsync<VRChatUnauthorizedException>(() => gate.RunAsync<object>(
            () => throw new global::VRChat.API.Client.ApiException(status, "secret response authToken=private"),
            TestContext.Current.CancellationToken, "好友快照"));
        Assert.Equal($"VRChat 好友快照 HTTP {status}", exception.Message);
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
        public Exception? ConnectFailure { get; set; }
        public int AuthenticationCalls { get; private set; }
        public int ConnectAttempts { get; private set; }
        public string? SnapshotAccountId { get; set; }
        public Func<CancellationToken, Task<VRChatAuthenticationState>>? Reauthentication { get; set; }
        public bool Verified { get; set; }
        public bool Expired { get; set; }
        public string AccountId { get; set; } = Account;
        public IVRChatApiSession FromCredentials(string username, string password) => this;
        public IVRChatApiSession FromSession(string serializedSession) => this;
        public Task<VRChatAuthenticationState> AuthenticateAsync(CancellationToken cancellationToken)
        {
            if (++AuthenticationCalls > 1 && Reauthentication is { } reauthenticate) return reauthenticate(cancellationToken);
            return Expired ? throw new VRChatUnauthorizedException("expired") :
                Task.FromResult(new VRChatAuthenticationState("Example", Verified ? [] : ["emailOtp"], Verified ? AccountId : null));
        }
        public Task VerifyTwoFactorAsync(string method, string code, CancellationToken cancellationToken)
        {
            Assert.Equal("emailOtp", method);
            if (code != "123456") throw new VRChatUnauthorizedException("wrong code");
            Verified = true;
            return Task.CompletedTask;
        }
        public Task<VRChatPresenceSnapshot> GetSnapshotAsync(CancellationToken token) =>
            Task.FromResult(new VRChatPresenceSnapshot(DateTimeOffset.UtcNow,
                [new(SnapshotAccountId ?? AccountId, "Example", new("wrld_test", "instance"), DateTimeOffset.UtcNow, "snapshot")]));
        public Task<IVRChatEventConnection> ConnectAsync(CancellationToken token)
        {
            ConnectAttempts++;
            if (ConnectFailure is { } failure) { ConnectFailure = null; throw failure; }
            return Task.FromResult<IVRChatEventConnection>(new Heartbeat.Testing.IdleVRChatConnection());
        }
        public Task<string?> GetWorldNameAsync(string worldId, CancellationToken cancellationToken) => Task.FromResult<string?>("World");
        public string ExportSession() => "session";
    }
}
