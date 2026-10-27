using System.Text.Json;
using Orisumi.Dev;

namespace Orisumi.Dev.Tests;

public sealed class CliContractTests
{
    [Test]
    [Arguments(0)]
    [Arguments(7)]
    [Arguments(130)]
    public async Task JsonExecutionReportsTheOutcomeWithoutProgressOnStdout(int exitCode)
    {
        using var fixture = new EnvironmentFixture();
        using var error = new StringWriter();
        var cli = new DeveloperCli(fixture.Repository, new CheckRunner(exitCode), fixture.Output, error);
        var actual = await cli.RunAsync(["verify", "checks", "--base", "HEAD", "--json"], CancellationToken.None);
        await Assert.That(actual).IsEqualTo(exitCode);
        using var result = JsonDocument.Parse(fixture.Output.ToString());
        await Assert.That(result.RootElement.GetProperty("exitCode").GetInt32()).IsEqualTo(exitCode);
        await Assert.That(result.RootElement.GetProperty("verification").GetProperty("results").GetArrayLength()).IsGreaterThan(0);
        await Assert.That(error.ToString().Contains("Running", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    [Arguments("")]
    [Arguments("env")]
    [Arguments("env telemetry")]
    [Arguments("env volumes")]
    [Arguments("mac")]
    [Arguments("verify")]
    [Arguments("analyze")]
    [Arguments("runs")]
    [Arguments("tool")]
    public async Task GroupsShowHelpWithoutPerformingOperations(string path)
    {
        using var fixture = new EnvironmentFixture();
        var cli = new DeveloperCli(fixture.Repository, fixture.Runner, fixture.Output, TextWriter.Null);
        await Assert.That(await cli.RunAsync(path.Split(' ', StringSplitOptions.RemoveEmptyEntries), CancellationToken.None)).IsEqualTo(0);
        await Assert.That(fixture.Runner.Calls.Count).IsEqualTo(0);
        await Assert.That(fixture.Output.ToString().Contains("orisumi", StringComparison.Ordinal)).IsTrue();
        await Assert.That(fixture.Output.ToString().Contains("Orisumi.Dev", StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    [Arguments("verify changed")]
    [Arguments("verify full")]
    [Arguments("quality --base HEAD")]
    [Arguments("loc")]
    [Arguments("install")]
    [Arguments("artifacts list")]
    [Arguments("env otel traces api")]
    [Arguments("mac build --debug")]
    [Arguments("mac run --configuration invalid")]
    [Arguments("verify checks --scope invalid")]
    [Arguments("verify checks --scope all --base HEAD")]
    [Arguments("verify checks --scope all --quality")]
    public async Task InvalidCommandsDoNotPerformOperations(string arguments)
    {
        using var fixture = new EnvironmentFixture();
        var cli = new DeveloperCli(fixture.Repository, fixture.Runner, fixture.Output, TextWriter.Null);
        await Assert.That(await cli.RunAsync(arguments.Split(' '), CancellationToken.None)).IsEqualTo(2);
        await Assert.That(fixture.Runner.Calls.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("logs")]
    [Arguments("spans")]
    [Arguments("traces")]
    public async Task TelemetryForwardsTheSelectedKindAndOptionalResource(string kind)
    {
        using var fixture = new EnvironmentFixture();
        var cli = new DeveloperCli(fixture.Repository, fixture.Runner, fixture.Output, TextWriter.Null);
        foreach (string[] target in new[] { Array.Empty<string>(), new[] { "migrate" } })
        {
            await Assert.That(await cli.RunAsync(["env", "telemetry", kind, .. target], CancellationToken.None)).IsEqualTo(0);
            await Assert.That(fixture.Runner.Calls.Last().Args.SequenceEqual(
                new[] { "otel", kind }.Concat(target).Concat(["--apphost", fixture.Manager.AppHost]))).IsTrue();
        }
    }

    [Test]
    public async Task VolumesListShowsAllCheckoutsWithoutDeletingAnything()
    {
        using var fixture = new EnvironmentFixture();
        fixture.Runner.VolumeInventory = true;
        var cli = new DeveloperCli(fixture.Repository, fixture.Runner, fixture.Output, TextWriter.Null);
        await Assert.That(await cli.RunAsync(["env", "volumes", "list"], CancellationToken.None)).IsEqualTo(0);
        using var result = JsonDocument.Parse(fixture.Output.ToString());
        await Assert.That(result.RootElement.GetArrayLength()).IsEqualTo(2);
        await Assert.That(fixture.Runner.Calls.Any(call => call.Args.Contains("rm"))).IsFalse();
    }

    [Test]
    [Arguments("changed", true)]
    [Arguments("all", false)]
    public async Task ScopePlansDoNotExecuteChecksOrCreateEvidence(string scope, bool quality)
    {
        using var fixture = new EnvironmentFixture();
        var runner = new CheckRunner(0);
        var cli = new DeveloperCli(fixture.Repository, runner, fixture.Output, TextWriter.Null);
        string[] selection = quality ? ["--base", "HEAD", "--quality"] : [];
        await Assert.That(await cli.RunAsync(["verify", "checks", "--scope", scope, .. selection, "--plan", "--json"], CancellationToken.None)).IsEqualTo(0);
        using var result = JsonDocument.Parse(fixture.Output.ToString());
        await Assert.That(result.RootElement.GetProperty("mode").GetString()).IsEqualTo(scope == "all" ? "full" : "changed");
        await Assert.That(result.RootElement.GetProperty("quality").GetBoolean()).IsEqualTo(quality);
        await Assert.That(runner.Calls.All(call => call.File == "git")).IsTrue();
        await Assert.That(new ArtifactStore(fixture.Repository).List().Count).IsEqualTo(0);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task DefaultChangedScopeOnlyInfersHeadForDirtyWorktrees(bool dirty)
    {
        using var fixture = new EnvironmentFixture();
        var runner = new CheckRunner(0) { Dirty = dirty };
        var cli = new DeveloperCli(fixture.Repository, runner, fixture.Output, TextWriter.Null);
        var code = await cli.RunAsync(["verify", "checks", "--plan", "--json"], CancellationToken.None);
        await Assert.That(code).IsEqualTo(dirty ? 0 : 2);
        if (!dirty) return;
        using var result = JsonDocument.Parse(fixture.Output.ToString());
        await Assert.That(result.RootElement.GetProperty("base").GetString()).IsEqualTo("HEAD");
    }

    [Test]
    public async Task CombinedJsonReportsQualityFailureSeparatelyFromPassingChecks()
    {
        using var fixture = new EnvironmentFixture();
        var runner = new CheckRunner(0);
        var cli = new DeveloperCli(fixture.Repository, runner, fixture.Output, TextWriter.Null);
        var code = await cli.RunAsync(["verify", "checks", "--base", "HEAD", "--quality", "--json"], CancellationToken.None);
        await Assert.That(code).IsEqualTo(1);
        using var result = JsonDocument.Parse(fixture.Output.ToString());
        await Assert.That(result.RootElement.GetProperty("verification").GetProperty("exitCode").GetInt32()).IsEqualTo(0);
        await Assert.That(result.RootElement.GetProperty("quality").GetProperty("completed").GetBoolean()).IsFalse();
        await Assert.That(new ArtifactStore(fixture.Repository).List().Count).IsEqualTo(1);
    }

    [Test]
    public async Task CancellationProducesAJsonResultAndPreservesPartialEvidence()
    {
        using var fixture = new EnvironmentFixture();
        var runner = new CheckRunner(0) { Cancel = true };
        var cli = new DeveloperCli(fixture.Repository, runner, fixture.Output, TextWriter.Null);
        await Assert.That(async () => await cli.RunAsync(["verify", "checks", "--base", "HEAD", "--json"], CancellationToken.None))
            .Throws<OperationCanceledException>();
        using var result = JsonDocument.Parse(fixture.Output.ToString());
        await Assert.That(result.RootElement.GetProperty("exitCode").GetInt32()).IsEqualTo(130);
        var run = new ArtifactStore(fixture.Repository).List().Single();
        await Assert.That(File.ReadAllText(Path.Combine(run.Directory, "dotnet-build.log"))).IsEqualTo("partial output");
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(run.Directory, "manifest.json")));
        await Assert.That(manifest.RootElement.GetProperty("exitCode").GetInt32()).IsEqualTo(130);
    }

    private sealed class CheckRunner(int exitCode) : IProcessRunner
    {
        public bool Dirty { get; init; }
        public bool Cancel { get; init; }
        public List<(string File, string[] Args)> Calls { get; } = [];
        public Task<ProcessResult> CaptureAsync(string fileName, IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string?>? environment, CancellationToken cancellationToken)
        {
            Calls.Add((fileName, arguments.ToArray()));
            if (fileName == "git") return Task.FromResult(new ProcessResult(0, arguments[0] switch
            {
                "diff" => "tools/Orisumi.Dev/DeveloperCli.cs\0",
                "rev-parse" => "1234567890abcdef",
                "status" when Dirty => " M tools/Orisumi.Dev/DeveloperCli.cs",
                _ => "",
            }, ""));
            if (Cancel) throw new CapturedProcessCancelledException("partial output", "", cancellationToken);
            return Task.FromResult(new ProcessResult(exitCode, "build or test output", ""));
        }
    }
}
