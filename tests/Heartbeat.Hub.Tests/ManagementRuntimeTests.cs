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
            using (var reopened = new HubLocalStorage(directory.FullName)) Assert.Equal(first, reopened.Id);
            File.WriteAllText(Path.Combine(directory.FullName, "hub-id"), "broken");
            Assert.Throws<InvalidDataException>(() => new HubLocalStorage(directory.FullName));
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task MultipleCollectorsRestoreWithoutLosingConfigurationAndPausePersists()
    {
        using var fixture = new ConfigurationDirectory();
        await using (var manager = fixture.CreateManager())
        {
            foreach (var target in new[] { "a", "b" })
            {
                await manager.ExecuteAsync(new("configure", "example", target, JsonSerializer.SerializeToElement(new { })), TestContext.Current.CancellationToken);
                await manager.ExecuteAsync(new("start", "example", target), TestContext.Current.CancellationToken);
            }
            await manager.ExecuteAsync(new("pause", "example", "a"), TestContext.Current.CancellationToken);
        }
        await using var restored = fixture.CreateManager();
        await restored.RestoreAsync(TestContext.Current.CancellationToken);
        Assert.Equal("paused", restored.Collectors.Single(x => x.Target == "a").State);
        Assert.Equal("running", restored.Collectors.Single(x => x.Target == "b").State);
        using var document = JsonDocument.Parse(File.ReadAllText(fixture.Path));
        Assert.Equal(2, document.RootElement.GetArrayLength());
    }

    [Fact]
    public async Task FailedReconfigurationDoesNotResumeAnOldEnabledSettingAfterRestart()
    {
        using var fixture = new ConfigurationDirectory();
        await using (var manager = fixture.CreateManager())
        {
            await manager.ExecuteAsync(new("configure", "example", "a", JsonSerializer.SerializeToElement(new { })), TestContext.Current.CancellationToken);
            await manager.ExecuteAsync(new("start", "example", "a"), TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<ArgumentException>(() => manager.ExecuteAsync(new("configure", "example", "a",
                JsonSerializer.SerializeToElement(new { invalid = true })), TestContext.Current.CancellationToken));
        }
        await using var restored = fixture.CreateManager();
        await restored.RestoreAsync(TestContext.Current.CancellationToken);
        Assert.Equal("paused", Assert.Single(restored.Collectors).State);
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
        public IManagedCollector Create(string target) => new TestCollector(target);
    }
    private sealed class TestCollector(string target) : IManagedCollector
    {
        private bool _running;
        public CollectorState State => new("example", target, "Example", _running ? "running" : "paused", null);
        public Task<JsonElement> ConfigureAsync(JsonElement configuration, CancellationToken cancellationToken) =>
            configuration.TryGetProperty("invalid", out _) ? throw new ArgumentException("Invalid configuration.") : Task.FromResult(configuration);
        public Task StartAsync(CancellationToken cancellationToken) { _running = true; return Task.CompletedTask; }
        public Task PauseAsync(CancellationToken cancellationToken) { _running = false; return Task.CompletedTask; }
        public Task RemoveAsync(CancellationToken cancellationToken) => PauseAsync(cancellationToken);
        public ValueTask DisposeAsync() { _running = false; return ValueTask.CompletedTask; }
    }
}
