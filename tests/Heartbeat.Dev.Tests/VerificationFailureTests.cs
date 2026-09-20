using System.Text.Json;
using Heartbeat.Dev;

namespace Heartbeat.Dev.Tests;

public sealed class VerificationFailureTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"heartbeat-failure-{Guid.NewGuid():N}");

    [Theory]
    [InlineData("verify", false)]
    [InlineData("verify", true)]
    [InlineData("scenario", false)]
    [InlineData("scenario", true)]
    [InlineData("quality", false)]
    [InlineData("quality", true)]
    [InlineData("native-desktop", false)]
    [InlineData("native-desktop", true)]
    [InlineData("collector-delivery", false)]
    [InlineData("collector-delivery", true)]
    [InlineData("desktop-replay", false)]
    [InlineData("desktop-replay", true)]
    public async Task RetainsFailureManifestWhenExecutionThrows(string command, bool cancel)
    {
        if (command is "native-desktop" or "collector-delivery" or "desktop-replay" && !OperatingSystem.IsMacOS()) return;
        var repository = new RepositoryContext(_root);
        Directory.CreateDirectory(repository.Path("src", "Frontend", "Heartbeat.Web", "node_modules"));
        File.WriteAllText(repository.Path(".env.local"), "HEARTBEAT_API_KEY=test\nHEARTBEAT_OWNER_ID=01952378-7bba-7b23-b092-ce581eb8f3ac\nHEARTBEAT_HUB_TOKEN=test\nHEARTBEAT_COLLECTOR_TARGET=test\n");
        var runner = new ThrowingRunner(cancel);
        var exception = await Record.ExceptionAsync(() => command switch
        {
            "verify" => new VerificationCommand(repository, runner, TextWriter.Null).RunAsync(new VerificationRequest("full", null, false, false), CancellationToken.None),
            "scenario" => new ScenarioCommand(repository, runner, TextWriter.Null).RunAsync(new ScenarioOptions("replay-fixture"), CancellationToken.None),
            "native-desktop" => new ScenarioCommand(repository, runner, TextWriter.Null).RunAsync(new ScenarioOptions("native-desktop"), CancellationToken.None),
            "collector-delivery" => new ScenarioCommand(repository, runner, TextWriter.Null).RunAsync(new ScenarioOptions("collector-delivery"), CancellationToken.None),
            "desktop-replay" => new ScenarioCommand(repository, runner, TextWriter.Null).RunAsync(new ScenarioOptions("desktop-replay"), CancellationToken.None),
            _ => new QualityCommand(repository, runner, TextWriter.Null).RunAsync(new QualityOptions("HEAD"), CancellationToken.None),
        });
        Assert.NotNull(exception);
        var run = Assert.Single(new ArtifactStore(repository).List());
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(run.Directory, "manifest.json")));
        Assert.Equal(cancel ? 130 : 1, manifest.RootElement.GetProperty("exitCode").GetInt32());
        Assert.Equal(cancel ? "cancelled" : "failed", manifest.RootElement.GetProperty("status").GetString());
        Assert.Equal(command is "native-desktop" or "collector-delivery" or "desktop-replay" ? "scenario" : command, manifest.RootElement.GetProperty("kind").GetString());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed class ThrowingRunner(bool cancel) : IProcessRunner
    {
        public Task<ProcessResult> CaptureAsync(string fileName, IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string?>? environment, CancellationToken cancellationToken) =>
            throw (cancel ? new OperationCanceledException() : new System.ComponentModel.Win32Exception("missing executable"));

        public Task<int> RunAsync(string fileName, IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string?>? environment, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
