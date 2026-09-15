using Heartbeat.Dev;

namespace Heartbeat.Dev.Tests;

public sealed class VerificationPlanningTests
{
    private static readonly RepositoryContext Repository = new("/repo");

    [Fact]
    public async Task DeveloperCliChangesSelectOnlyCliTests()
    {
        var plan = await PlanAsync("tools/Heartbeat.Dev/Program.cs\0tests/Heartbeat.Dev.Tests/X.cs\0");

        var step = Assert.Single(plan.Steps);
        Assert.Equal("developer-cli", step.Name);
    }

    [Fact]
    public async Task ReplayUiChangesSelectWebAndBrowserChecks()
    {
        var plan = await PlanAsync("src/Frontend/Heartbeat.Web/src/app/page.tsx\0");

        Assert.Equal(["web", "browser"], plan.Steps.Select(step => step.Name));
    }

    [Fact]
    public async Task UnknownPathsFallBackToFullVerification()
    {
        var plan = await PlanAsync("compose.yaml\0");

        Assert.Equal("full-fallback", plan.Mode);
        Assert.Equal(["dotnet", "web", "browser"], plan.Steps.Select(step => step.Name));
    }

    private static Task<VerificationPlan> PlanAsync(string trackedPaths)
    {
        var runner = new StubRunner([
            new ProcessResult(0, trackedPaths, string.Empty),
            new ProcessResult(0, string.Empty, string.Empty),
        ]);
        return VerificationPlanner.CreateAsync(
            Repository, runner, new VerificationRequest("changed", "HEAD", false, false), CancellationToken.None);
    }

    private sealed class StubRunner(IEnumerable<ProcessResult> results) : IProcessRunner
    {
        private readonly Queue<ProcessResult> _results = new(results);

        public Task<ProcessResult> CaptureAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string?>? environment,
            CancellationToken cancellationToken) => Task.FromResult(_results.Dequeue());

        public Task<int> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string?>? environment,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}

public sealed class VerificationCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"heartbeat-verify-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task WritesLogsAndManifestUnderVerificationArtifacts()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, "src", "Frontend", "Heartbeat.Web", "node_modules"));
        var runner = new SuccessfulRunner();
        var output = new StringWriter();
        var command = new VerificationCommand(new RepositoryContext(_root), runner, output);

        var exitCode = await command.RunAsync(["full"], CancellationToken.None);

        Assert.Equal(0, exitCode);
        var run = Assert.Single(new ArtifactStore(new RepositoryContext(_root)).List());
        Assert.True(File.Exists(Path.Combine(run.Directory, "manifest.json")));
        Assert.True(File.Exists(Path.Combine(run.Directory, "dotnet.log")));
        Assert.True(File.Exists(Path.Combine(run.Directory, "web.log")));
        Assert.True(File.Exists(Path.Combine(run.Directory, "browser.log")));
        Assert.Contains("Verification evidence:", output.ToString(), StringComparison.Ordinal);
        var browser = Assert.Single(runner.Environments, item => item?.ContainsKey("HEARTBEAT_EVIDENCE_DIR") == true);
        Assert.StartsWith(run.Directory, browser!["HEARTBEAT_EVIDENCE_DIR"], StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class SuccessfulRunner : IProcessRunner
    {
        public List<IReadOnlyDictionary<string, string?>?> Environments { get; } = [];

        public Task<ProcessResult> CaptureAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string?>? environment,
            CancellationToken cancellationToken)
        {
            Environments.Add(environment);
            return Task.FromResult(new ProcessResult(0, $"passed: {fileName}", string.Empty));
        }

        public Task<int> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string?>? environment,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
