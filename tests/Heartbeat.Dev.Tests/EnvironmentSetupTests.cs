using Heartbeat.Dev;

namespace Heartbeat.Dev.Tests;

public sealed class EnvironmentSetupTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("heartbeat-setup-").FullName;
    private const string Owner = "b4414616-89de-47a5-b190-c2f8674c33b6";
    private string EnvPath => Path.Combine(_root, ".env.local");

    [Fact]
    public async Task ValidatedConfigurationIsCommittedOnceWithoutPrintingSecrets()
    {
        var original = $"# preserve me\nCUSTOM='untouched'\nHEARTBEAT_OWNER_ID='{Owner.ToUpperInvariant()}'\n";
        File.WriteAllText(EnvPath, original);
        const string apiKey = "a$secret'with\\quotes";
        var input = new SetupInput(["y", apiKey, "my-target", "My PC"]);
        var runner = new SetupRunner(EnvPath, original);
        using var output = new StringWriter();
        await new EnvironmentSetup(new RepositoryContext(_root), runner, input, output).RunAsync(CancellationToken.None);

        var saved = DotenvFile.Read(EnvPath);
        Assert.Equal(apiKey, saved.GetSaved("HEARTBEAT_API_KEY"));
        Assert.Equal(Owner, saved.GetSaved("HEARTBEAT_OWNER_ID"));
        Assert.Equal("my-target", saved.GetSaved("HEARTBEAT_COLLECTOR_TARGET"));
        Assert.Equal("My PC", saved.GetSaved("HEARTBEAT_COLLECTOR_DISPLAY_NAME"));
        Assert.Equal("untouched", saved.GetSaved("CUSTOM"));
        Assert.StartsWith("# preserve me", File.ReadAllText(EnvPath));
        Assert.Equal(64, saved.GetSaved("HEARTBEAT_HUB_TOKEN")!.Length);
        Assert.DoesNotContain(apiKey, output.ToString());
        Assert.DoesNotContain(saved.GetSaved("HEARTBEAT_HUB_TOKEN")!, output.ToString());
        Assert.Equal([false, true, false, false], input.SecretPrompts);
        Assert.Equal(2, runner.Calls);
        Assert.Equal([EnvPath], Directory.GetFiles(_root));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(EnvPath));
    }

    [Theory]
    [InlineData("build")]
    [InlineData("auth")]
    [InlineData("malformed")]
    [InlineData("owner")]
    [InlineData("cancel")]
    public async Task FailurePreservesConfigurationAndRemovesStagedSecrets(string failure)
    {
        var original = $"HEARTBEAT_OWNER_ID='{Owner}'\nHEARTBEAT_API_KEY='saved-key'\n";
        File.WriteAllText(EnvPath, original);
        var input = new SetupInput(["y", "replacement-key"], cancelAfterInputs: failure == "cancel");
        var runner = new SetupRunner(EnvPath, original, failure);
        var setup = new EnvironmentSetup(new RepositoryContext(_root), runner, input, TextWriter.Null);
        if (failure == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => setup.RunAsync(CancellationToken.None));
        else await Assert.ThrowsAsync<InvalidOperationException>(() => setup.RunAsync(CancellationToken.None));
        Assert.Equal(original, File.ReadAllText(EnvPath));
        Assert.Equal([EnvPath], Directory.GetFiles(_root));
    }

    [Fact]
    public async Task RerunKeepsSavedValuesAndTheSeparateHubToken()
    {
        var hubToken = new string('A', 64);
        var original = $"HEARTBEAT_API_KEY='keep-key'\nHEARTBEAT_HUB_TOKEN='{hubToken}'\nHEARTBEAT_COLLECTOR_TARGET='keep-target'\nHEARTBEAT_COLLECTOR_DISPLAY_NAME='Keep name'\n";
        File.WriteAllText(EnvPath, original);
        using var output = new StringWriter();
        var runner = new SetupRunner(EnvPath, original);
        await new EnvironmentSetup(new RepositoryContext(_root), runner,
            new SetupInput(["y", "", "", ""]), output).RunAsync(CancellationToken.None);
        Assert.Contains("Existing .env.local found", output.ToString());
        Assert.Contains("Building Hub authentication checker", output.ToString());
        Assert.Contains("Validating API key with Auth", output.ToString());
        Assert.Equal(0, runner.BrowserOpens);
        var saved = DotenvFile.Read(EnvPath);
        Assert.Equal("keep-key", saved.GetSaved("HEARTBEAT_API_KEY"));
        Assert.Equal(hubToken, saved.GetSaved("HEARTBEAT_HUB_TOKEN"));
        Assert.Equal("keep-target", saved.GetSaved("HEARTBEAT_COLLECTOR_TARGET"));
        Assert.Equal("Keep name", saved.GetSaved("HEARTBEAT_COLLECTOR_DISPLAY_NAME"));
    }

    [Fact]
    public async Task RepeatSetupDefaultsToKeepingConfigurationWithoutOpeningBrowserOrRunningDocker()
    {
        const string original = "HEARTBEAT_API_KEY='saved-secret'\n";
        File.WriteAllText(EnvPath, original);
        var runner = new SetupRunner(EnvPath, original);
        using var output = new StringWriter();
        await new EnvironmentSetup(new RepositoryContext(_root), runner,
            new SetupInput([""]), output).RunAsync(CancellationToken.None);
        Assert.Contains("Existing .env.local found", output.ToString());
        Assert.Contains("unchanged", output.ToString());
        Assert.Equal(0, runner.Calls);
        Assert.Equal(0, runner.BrowserOpens);
        Assert.Equal(original, File.ReadAllText(EnvPath));
        Assert.Equal([EnvPath], Directory.GetFiles(_root));
    }

    [Fact]
    public void ConcurrentEditCannotBeOverwrittenBySetup()
    {
        File.WriteAllText(EnvPath, "CUSTOM=before\n");
        using (var configuration = new SetupConfiguration(EnvPath))
        {
            configuration.Set("HEARTBEAT_API_KEY", "new-secret");
            configuration.SaveStaging();
            File.WriteAllText(EnvPath, "CUSTOM=changed-elsewhere\n");
            Assert.Throws<InvalidOperationException>(configuration.Commit);
        }
        Assert.Equal("CUSTOM=changed-elsewhere\n", File.ReadAllText(EnvPath));
        Assert.Equal([EnvPath], Directory.GetFiles(_root));
    }

    [Fact]
    public void SetupRejectsSymbolicLinksWithoutTouchingTheirTargets()
    {
        if (OperatingSystem.IsWindows()) return; // Windows link creation can require an elevated developer session.
        var target = Path.Combine(_root, "target.env");
        File.WriteAllText(target, "unchanged");
        File.CreateSymbolicLink(EnvPath, target);
        Assert.Throws<InvalidOperationException>(() => new SetupConfiguration(EnvPath));
        Assert.Equal("unchanged", File.ReadAllText(target));
    }

    public void Dispose() => Directory.Delete(_root, true);

    private sealed class SetupInput(string[] values, bool cancelAfterInputs = false) : ISetupInput
    {
        private int _next;
        public List<bool> SecretPrompts { get; } = [];
        public Task<string> ReadAsync(string prompt, bool secret, CancellationToken token)
        {
            if (cancelAfterInputs && _next == values.Length) throw new OperationCanceledException();
            SecretPrompts.Add(secret);
            return Task.FromResult(values[_next++]);
        }
    }

    private sealed class SetupRunner(string finalPath, string original, string? failure = null) : IProcessRunner
    {
        public int Calls { get; private set; }
        public int BrowserOpens { get; private set; }
        public void OpenApplication(string path)
        {
            Assert.Equal("https://auth.shenxianovo.com/dashboard/api-keys", path);
            BrowserOpens++;
        }
        public Task<ProcessResult> CaptureAsync(string fileName, IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string?>? environment, CancellationToken cancellationToken)
        {
            Calls++;
            Assert.Equal("docker", fileName);
            Assert.Equal(original, File.ReadAllText(finalPath));
            if (arguments[0] == "rm") return Task.FromResult(new ProcessResult(0, "", ""));
            var envFile = arguments[arguments.ToList().IndexOf("--env-file") + 1];
            Assert.Contains(".env.local.setup.", envFile);
            Assert.NotNull(DotenvFile.Read(envFile).GetSaved("HEARTBEAT_API_KEY"));
            Assert.NotNull(environment);
            Assert.Null(environment["HEARTBEAT_API_KEY"]);
            Assert.Null(environment["HEARTBEAT_OWNER_ID"]);
            if (failure == "build" || failure == "auth" && Calls == 2)
                return Task.FromResult(new ProcessResult(7, "", "must not leak secret output"));
            var result = failure switch
            {
                "malformed" => "{\"ownerId\":\"invalid\"}",
                "owner" => "{\"ownerId\":\"97e4e9f2-e79f-4d18-85e8-8cc7f09f0296\"}",
                _ => $"{{\"ownerId\":\"{Owner}\"}}",
            };
            return Task.FromResult(new ProcessResult(0, Calls == 1 ? "built" : result, ""));
        }
        public Task<int> RunAsync(string fileName, IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string?>? environment, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
