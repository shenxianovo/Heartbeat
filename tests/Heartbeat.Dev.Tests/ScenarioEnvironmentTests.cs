using System.Text.Json;
using Heartbeat.Dev;

namespace Heartbeat.Dev.Tests;

public sealed class ScenarioEnvironmentTests : IDisposable
{
    private readonly RepositoryContext _repository = new(Path.Combine(Path.GetTempPath(), $"heartbeat-environment-{Guid.NewGuid():N}"));

    [Theory]
    [InlineData(false, false, 0)]
    [InlineData(true, false, 0)]
    [InlineData(false, true, 1)]
    public async Task SuccessCleansSharedResourcesAndCleanupFailureFailsTheRun(bool keepOnFailure, bool cleanupFails, int expected)
    {
        Prepare();
        var compose = new ComposeSimulator(cleanupFails);
        var exit = await RunAsync(compose, keepOnFailure, async environment =>
        {
            // One lifetime can contain independently requested dependencies.
            await environment.StartAsync(CancellationToken.None, ComposeService.Hub);
            await environment.StartAsync(CancellationToken.None, ComposeService.Database);
            await environment.WriteAsync("assertions.json", new { passed = true }, CancellationToken.None);
            return 0;
        });

        Assert.Equal(expected, exit);
        Assert.Equal(cleanupFails, compose.EnvironmentExists);
        using var manifest = Manifest();
        Assert.Equal(expected == 0 ? "succeeded" : "failed", manifest.RootElement.GetProperty("status").GetString());
        Assert.Contains(manifest.RootElement.GetProperty("artifacts").EnumerateArray(), item => item.GetString() == "assertions.json");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task FailureOrCancellationRetainsResourcesOnlyWhenRequested(bool keep, bool cancel)
    {
        Prepare();
        var compose = new ComposeSimulator(cleanupFails: false);
        var error = await Record.ExceptionAsync(() => RunAsync(compose, keep, async environment =>
        {
            await environment.StartAsync(CancellationToken.None, ComposeService.Hub);
            throw cancel ? new OperationCanceledException() : new InvalidOperationException("scenario assertion failed");
        }));

        Assert.NotNull(error);
        Assert.Equal(keep, compose.EnvironmentExists);
        using var manifest = Manifest();
        Assert.Equal(cancel ? "cancelled" : "failed", manifest.RootElement.GetProperty("status").GetString());
        if (keep) Assert.Contains(manifest.RootElement.GetProperty("limitations").EnumerateArray(), item => item.GetString()!.Contains("retained", StringComparison.Ordinal));
    }

    private Task<int> RunAsync(IProcessRunner runner, bool keep, Func<ScenarioEnvironment, Task<int>> action) =>
        ScenarioEnvironment.RunAsync(_repository, runner, TextWriter.Null,
            new ScenarioOptions("composition-test", false, keep), [], action, CancellationToken.None);

    private void Prepare()
    {
        Directory.CreateDirectory(_repository.Root);
        File.WriteAllText(_repository.Path(".env.local"), "HEARTBEAT_API_KEY=test\nHEARTBEAT_OWNER_ID=01952378-7bba-7b23-b092-ce581eb8f3ac\n");
    }

    private JsonDocument Manifest()
    {
        var run = Assert.Single(new ArtifactStore(_repository).List());
        return JsonDocument.Parse(File.ReadAllText(Path.Combine(run.Directory, "manifest.json")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_repository.Root)) Directory.Delete(_repository.Root, recursive: true);
    }

    // Models the external Compose resource lifetime; real native scenarios verify Docker wiring.
    private sealed class ComposeSimulator(bool cleanupFails) : IProcessRunner
    {
        public bool EnvironmentExists { get; private set; }

        public Task<ProcessResult> CaptureAsync(string fileName, IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string?>? environment, CancellationToken cancellationToken)
        {
            if (arguments.Contains("up")) EnvironmentExists = true;
            if (arguments.Contains("down"))
            {
                if (cleanupFails) return Task.FromResult(new ProcessResult(1, "", "cleanup failed"));
                EnvironmentExists = false;
            }
            return Task.FromResult(new ProcessResult(0, "", ""));
        }

        public Task<int> RunAsync(string fileName, IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string?>? environment, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
