using Heartbeat.Dev;

namespace Heartbeat.Dev.Tests;

public sealed class DeveloperCliTests
{
    [Test]
    [Arguments("api", "api,nginx")]
    [Arguments("docs", "docs,nginx")]
    [Arguments("db", "db")]
    public async Task StartupSelectsOnlyTheRequestedStackAndSharedEntry(string target, string expected)
    {
        var plan = EnvironmentPlan.Create(new EnvironmentOptions(EnvironmentAction.Up, false, null,
            false, false, new HashSet<string> { target }));
        await Assert.That(string.Join(',', plan.ComposeServices)).IsEqualTo(expected);
    }

    [Test]
    [Arguments("api", "api,migrate")]
    [Arguments("docs", "docs")]
    [Arguments("db", "db")]
    public async Task StopPreservesUnselectedSharedServices(string target, string expected)
    {
        var runner = new RecordingRunner();
        var cli = new DeveloperCli(new RepositoryContext(Path.GetTempPath()), runner, TextWriter.Null, TextWriter.Null);
        await Assert.That(await cli.RunAsync(["env", "down", target], CancellationToken.None)).IsEqualTo(0);
        var invocation = runner.Calls.Single(call => call.Arguments.Contains("rm"));
        await Assert.That(string.Join(',', invocation.Arguments.SkipWhile(value => value != "--force").Skip(1)))
            .IsEqualTo(expected);
        await Assert.That(invocation.Arguments.Contains("--volumes")).IsFalse();
    }

    [Test]
    public async Task WholeStackStopUsesComposeDownWithoutDeletingVolumes()
    {
        var runner = new RecordingRunner();
        var cli = new DeveloperCli(new RepositoryContext(Path.GetTempPath()), runner, TextWriter.Null, TextWriter.Null);
        await Assert.That(await cli.RunAsync(["env", "down"], CancellationToken.None)).IsEqualTo(0);
        await Assert.That(runner.Calls.Single().Arguments.Contains("down")).IsTrue();
        await Assert.That(runner.Calls.Single().Arguments.Contains("--volumes")).IsFalse();
    }

    [Test]
    public async Task InvalidTargetDoesNotRunAnyProcesses()
    {
        var runner = new RecordingRunner();
        var cli = new DeveloperCli(new RepositoryContext(Path.GetTempPath()), runner, TextWriter.Null, TextWriter.Null);
        await Assert.That(await cli.RunAsync(["env", "up", "hub"], CancellationToken.None)).IsEqualTo(2);
        await Assert.That(runner.Calls.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("src/Docs/Heartbeat.Docs/content/docs/core/index.mdx", "docs-types,docs-build")]
    [Arguments("src/Backend/Heartbeat.Api/Program.cs", "dotnet-build,integration-tests")]
    [Arguments("tools/Heartbeat.Dev/DeveloperCli.cs", "dotnet-build,developer-cli-tests")]
    [Arguments("unclassified.config", "dotnet-build,integration-tests,developer-cli-tests,docs-types,docs-build")]
    public async Task ChangedVerificationSelectsCurrentChecks(string path, string expected)
    {
        var runner = new RecordingRunner { ChangedPath = path };
        var plan = await VerificationPlanner.CreateAsync(new RepositoryContext(Path.GetTempPath()), runner,
            new VerificationRequest("changed", "HEAD", true, false), CancellationToken.None);
        await Assert.That(string.Join(',', plan.Steps.Select(step => step.Name))).IsEqualTo(expected);
    }

    private sealed class RecordingRunner : IProcessRunner
    {
        public string? ChangedPath { get; init; }
        public List<(string FileName, IReadOnlyList<string> Arguments)> Calls { get; } = [];

        public Task<ProcessResult> CaptureAsync(string fileName, IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string?>? environment, CancellationToken cancellationToken)
        {
            Calls.Add((fileName, arguments));
            return Task.FromResult(new ProcessResult(0,
                fileName == "git" && arguments[0] == "diff" && ChangedPath is not null ? ChangedPath + '\0' : "", ""));
        }

        public Task<int> RunAsync(string fileName, IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string?>? environment, CancellationToken cancellationToken)
        {
            Calls.Add((fileName, arguments));
            return Task.FromResult(0);
        }
    }
}
