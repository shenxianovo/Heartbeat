using System.Text.Json;
using Heartbeat.Dev;

namespace Heartbeat.Dev.Tests;

public sealed class VerificationExecutionTests : IDisposable
{
    private readonly RepositoryContext _repository = new(Path.Combine(Path.GetTempPath(), $"heartbeat-execution-{Guid.NewGuid():N}"));

    [Theory]
    [InlineData(7, 0, 0, 7)]
    [InlineData(7, 9, 0, 7)]
    [InlineData(0, 9, 11, 9)]
    public async Task RunsAllChecksAndRetainsTheFirstFailure(int dotnet, int web, int browser, int expectedExitCode)
    {
        int[] codes = [dotnet, web, browser];
        string[] names = ["dotnet", "web", "browser"];
        var runner = new StepRunner(index => new ProcessResult(codes[index], $"stdout-{index}", $"stderr-{index}"));
        var output = new StringWriter();

        var exitCode = await Command(runner, output).RunAsync(["verify", "full"], CancellationToken.None);

        Assert.Equal(3, runner.Calls);
        Assert.Equal(expectedExitCode, exitCode);
        var run = Assert.Single(new ArtifactStore(_repository).List());
        using var manifest = ReadManifest(run);
        Assert.Equal(expectedExitCode, manifest.RootElement.GetProperty("exitCode").GetInt32());
        Assert.Equal("failed", manifest.RootElement.GetProperty("status").GetString());
        Assert.Equal(3, manifest.RootElement.GetProperty("commands").GetArrayLength());
        Assert.Contains("Verification summary:", output.ToString(), StringComparison.Ordinal);
        for (var index = 0; index < names.Length; index++)
        {
            var log = Path.Combine(run.Directory, $"{names[index]}.log");
            Assert.Equal($"stdout-{index}stderr-{index}", File.ReadAllText(log));
            var status = codes[index] == 0 ? "succeeded" : "failed";
            Assert.Contains($"{names[index]}: {status} (exit {codes[index]}); log: {log}", output.ToString(), StringComparison.Ordinal);
            Assert.Contains(manifest.RootElement.GetProperty("artifacts").EnumerateArray(), item => item.GetString() == $"{names[index]}.log");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecutionExceptionsStopLaterChecksAndOverrideEarlierFailure(bool cancel)
    {
        var runner = new StepRunner(index => index == 0
            ? new ProcessResult(7, "failed", "")
            : throw (cancel ? new OperationCanceledException() : new System.ComponentModel.Win32Exception("missing executable")));

        var exception = await Record.ExceptionAsync(() => Command(runner, TextWriter.Null).RunAsync(["verify", "full"], CancellationToken.None));

        Assert.NotNull(exception);
        Assert.Equal(2, runner.Calls);
        var run = Assert.Single(new ArtifactStore(_repository).List());
        using var manifest = ReadManifest(run);
        Assert.Equal(cancel ? 130 : 1, manifest.RootElement.GetProperty("exitCode").GetInt32());
        Assert.Equal(cancel ? "cancelled" : "failed", manifest.RootElement.GetProperty("status").GetString());
        Assert.True(File.Exists(Path.Combine(run.Directory, "dotnet.log")));
        Assert.False(File.Exists(Path.Combine(run.Directory, "browser.log")));
    }

    [Fact]
    public async Task CancellationExitCodeStopsLaterChecksAndOverridesEarlierFailure()
    {
        var runner = new StepRunner(index => new ProcessResult(index == 0 ? 7 : 130, "output", ""));

        var exitCode = await Command(runner, TextWriter.Null).RunAsync(["verify", "full"], CancellationToken.None);

        Assert.Equal(130, exitCode);
        Assert.Equal(2, runner.Calls);
        var run = Assert.Single(new ArtifactStore(_repository).List());
        using var manifest = ReadManifest(run);
        Assert.Equal("cancelled", manifest.RootElement.GetProperty("status").GetString());
        Assert.True(File.Exists(Path.Combine(run.Directory, "web.log")));
        Assert.False(File.Exists(Path.Combine(run.Directory, "browser.log")));
    }

    private DeveloperCli Command(IProcessRunner runner, TextWriter output)
    {
        Directory.CreateDirectory(_repository.Path("src", "Frontend", "Heartbeat.Web", "node_modules"));
        return new DeveloperCli(_repository, runner, output, TextWriter.Null);
    }

    private static JsonDocument ReadManifest(ArtifactRun run) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(run.Directory, "manifest.json")));

    public void Dispose()
    {
        if (Directory.Exists(_repository.Root)) Directory.Delete(_repository.Root, recursive: true);
    }

    private sealed class StepRunner(Func<int, ProcessResult> execute) : IProcessRunner
    {
        public int Calls { get; private set; }

        public Task<ProcessResult> CaptureAsync(string fileName, IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string?>? environment, CancellationToken cancellationToken) =>
            Task.FromResult(execute(Calls++));

        public Task<int> RunAsync(string fileName, IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string?>? environment, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
