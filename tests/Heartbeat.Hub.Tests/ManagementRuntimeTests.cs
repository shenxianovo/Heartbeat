using System.Text.Json;
using Heartbeat.Hub.Runtime;
using Heartbeat.Management;

namespace Heartbeat.Hub.Tests;

public sealed class ManagementRuntimeTests
{
    [Fact]
    public void HubIdentitySurvivesRestartAndRejectsCorruption()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            Guid first;
            using (var storage = new HubLocalStorage(directory.FullName)) first = storage.Id;
            Assert.Equal(7, first.Version);
            using (var reopened = new HubLocalStorage(directory.FullName)) Assert.Equal(first, reopened.Id);
            File.WriteAllText(Path.Combine(directory.FullName, "hub-id"), "broken");
            Assert.Throws<InvalidDataException>(() => new HubLocalStorage(directory.FullName));
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task AuthenticatedAccountsRestoreAndUnfinishedLoginsNeverBecomeCollectors()
    {
        using var fixture = new ConfigurationDirectory();
        await using (var manager = fixture.CreateManager())
        {
            foreach (var account in new[] { "a", "b" })
                await manager.LoginAsync(new("example", JsonSerializer.SerializeToElement(new { account })), TestContext.Current.CancellationToken);
            var pending = await manager.LoginAsync(new("example", JsonSerializer.SerializeToElement(new { pending = true })), TestContext.Current.CancellationToken);
            Assert.NotNull(pending.SessionId);
            Assert.Equal(2, manager.Collectors.Count);
        }
        await using var restored = fixture.CreateManager();
        await restored.RestoreAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["a", "b"], restored.Collectors.Select(x => x.Target).Order().ToArray());
        Assert.All(restored.Collectors, x => Assert.Equal("running", x.State));
        using var document = JsonDocument.Parse(File.ReadAllText(fixture.Path));
        Assert.Equal(2, document.RootElement.GetArrayLength());
    }

    [Fact]
    public async Task SupersededLoginCannotSubmitCodeIntoAnotherSession()
    {
        using var fixture = new ConfigurationDirectory();
        await using var manager = fixture.CreateManager();
        var request = new CollectorLoginRequest("example", JsonSerializer.SerializeToElement(new { pending = true }));
        var previous = await manager.LoginAsync(request, TestContext.Current.CancellationToken);
        var next = await manager.LoginAsync(request, TestContext.Current.CancellationToken);
        Assert.NotEqual(previous.SessionId, next.SessionId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.LoginAsync(request with {
            SessionId = previous.SessionId, Input = JsonSerializer.SerializeToElement(new { account = "wrong" })
        }, TestContext.Current.CancellationToken));
        Assert.Empty(manager.Collectors);
    }

    [Fact]
    public void SecretsAreNotPlaintextAndSurviveRestart()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            new LocalSecretStore(directory.FullName).Write("example", "private-session");
            Assert.Equal("private-session", new LocalSecretStore(directory.FullName).Read("example"));
            Assert.All(directory.GetFiles(), file => Assert.DoesNotContain("private-session", File.ReadAllText(file.FullName)));
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public void EachHubKeepsItsOwnDocumentsSecretsAndIdentityUnderOneRoot()
    {
        var first = Directory.CreateTempSubdirectory();
        var second = Directory.CreateTempSubdirectory();
        try
        {
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(first.FullName,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.OtherRead);
            using (var one = new HubLocalStorage(first.FullName))
            using (var two = new HubLocalStorage(second.FullName))
            {
                if (!OperatingSystem.IsWindows()) Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                    File.GetUnixFileMode(first.FullName));
                Assert.NotEqual(one.Id, two.Id);
                one.WriteDocument("example", "first");
                two.WriteDocument("example", "second");
                one.Secrets.Write("account", "session-one");
                Assert.Null(two.Secrets.Read("account"));
                Assert.Equal(first.FullName, System.IO.Path.GetDirectoryName(one.DatabasePath));
                Assert.Throws<IOException>(() => new HubLocalStorage(first.FullName));
                Assert.Throws<ArgumentException>(() => one.WriteDocument("../escape", new { }));
            }
            using var reopened = new HubLocalStorage(first.FullName);
            Assert.Equal("first", reopened.ReadDocument<string>("example"));
            Assert.Equal("session-one", reopened.Secrets.Read("account"));
        }
        finally { first.Delete(true); second.Delete(true); }
    }

    private sealed class ConfigurationDirectory : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory();
        public string Path => System.IO.Path.Combine(_directory.FullName, "collectors.json");
        private HubLocalStorage? _storage;
        public CollectorManager CreateManager() => new(_storage ??= new HubLocalStorage(_directory.FullName), [new TestFactory()]);
        public void Dispose() { _storage?.Dispose(); _directory.Delete(true); }
    }

    private sealed class TestFactory : ICollectorFactory
    {
        public CollectorType Type => new("example", "Example", []);
        public ICollectorSession Create(string? target) => new TestCollector(target);
    }
    private sealed class TestCollector(string? target) : ICollectorSession
    {
        private string? _target = target;
        private bool _running;
        public CollectorState? State => _target is null ? null : new("example", _target, "Example", _running ? "running" : "paused", null);
        public Task<CollectorLoginResult> LoginAsync(JsonElement input, CancellationToken token)
        {
            if (input.TryGetProperty("account", out var account)) _target = account.GetString();
            return Task.FromResult(new CollectorLoginResult(_target, []));
        }
        public Task RestoreAsync(CancellationToken token) => StartAsync(token);
        public Task StartAsync(CancellationToken cancellationToken) { _running = true; return Task.CompletedTask; }
        public ValueTask DisposeAsync() { _running = false; return ValueTask.CompletedTask; }
    }
}
