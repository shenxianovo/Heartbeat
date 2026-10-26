using Orisumi.Dev;

namespace Orisumi.Dev.Tests;

public sealed class DeveloperCliTests
{
    [Test]
    [Arguments("src/Docs/Orisumi.Docs/content/docs/core/index.mdx", "docs-check,docs-test,docs-types,docs-build")]
    [Arguments("AGENTS.md", "docs-check")]
    [Arguments("notes/README.md", "docs-check")]
    [Arguments("src/Backend/Orisumi.Api/README.md", "dotnet-build,model-tests,integration-tests,docs-check")]
    [Arguments("src/Backend/Orisumi.Api/Program.cs", "dotnet-build,model-tests,integration-tests")]
    [Arguments("tests/Orisumi.Model.Tests/JsonAmbiguityTests.cs", "dotnet-build,model-tests")]
    [Arguments("tools/Orisumi.Dev/DeveloperCli.cs", "dotnet-build,developer-cli-tests")]
    [Arguments("src/Observers/Orisumi.Observer.ForegroundState/Program.cs", "dotnet-build,integration-tests")]
    [Arguments("tools/Orisumi.AppHost/Program.cs", "dotnet-build")]
    [Arguments("docker/nginx/api-enabled.conf", "dotnet-build")]
    [Arguments("src/Clients/Orisumi.Web/src/main.tsx", "frontend-verify")]
    [Arguments("src/Clients/Orisumi.Mac/Orisumi/MainView.swift", "mac-test,mac-build")]
    [Arguments("src/Clients/Orisumi.Mac/README.md", "mac-test,mac-build,docs-check")]
    [Arguments("unclassified.config", "dotnet-build,model-tests,integration-tests,developer-cli-tests,frontend-verify,docs-check,docs-test,docs-types,docs-build")]
    public async Task ChangedVerificationSelectsCurrentChecks(string path, string expected)
    {
        // A full run adds the macOS client checks on macOS.
        if (path == "unclassified.config" && OperatingSystem.IsMacOS()) expected += ",mac-test,mac-build";
        var runner = new RecordingRunner { ChangedPath = path };
        var plan = await VerificationPlanner.CreateAsync(new RepositoryContext(Path.GetTempPath()), runner,
            new VerificationRequest("changed", "HEAD", true, false), CancellationToken.None);
        await Assert.That(string.Join(',', plan.Steps.Select(step => step.Name))).IsEqualTo(expected);
    }

    private sealed class RecordingRunner : IProcessRunner
    {
        public string? ChangedPath { get; init; }

        public Task<ProcessResult> CaptureAsync(string fileName, IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string?>? environment, CancellationToken cancellationToken)
            => Task.FromResult(new ProcessResult(0,
                fileName == "git" && arguments[0] == "diff" && ChangedPath is not null ? ChangedPath + '\0' : "", ""));
    }

    [Test]
    public async Task MacClientGetsOneProgramInstancePerVolume()
    {
        var repository = new RepositoryContext("/repo");
        var command = new MacCommand(repository, new RecordingRunner(), TextWriter.Null);
        var status = new EnvironmentStatus("/repo", true, [new("api", "Running")],
            new("http://localhost:51065/api", "http://localhost:51066/", "http://localhost:51067/"), null,
            new("Host=localhost", "orisumi-temp-1", true));
        await Assert.That(string.Join(' ', command.LaunchArguments(status, window: true))).IsEqualTo(
            "-backend http://localhost:51065/api/ -webConsole http://localhost:51067/ " +
            "-dataDirectory /repo/.artifacts/mac/instances/orisumi-temp-1 -showWindow YES");
        await Assert.That(() => command.LaunchArguments(new EnvironmentStatus("/repo", false), window: false))
            .Throws<CommandUsageException>();
    }
}
