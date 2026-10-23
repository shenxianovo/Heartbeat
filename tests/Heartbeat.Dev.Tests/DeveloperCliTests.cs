using Heartbeat.Dev;

namespace Heartbeat.Dev.Tests;

public sealed class DeveloperCliTests
{
    [Test]
    [Arguments("src/Docs/Heartbeat.Docs/content/docs/core/index.mdx", "docs-check,docs-test,docs-types,docs-build")]
    [Arguments("AGENTS.md", "docs-check")]
    [Arguments("notes/README.md", "docs-check")]
    [Arguments("src/Backend/Heartbeat.Api/README.md", "dotnet-build,model-tests,integration-tests,docs-check")]
    [Arguments("src/Backend/Heartbeat.Api/Program.cs", "dotnet-build,model-tests,integration-tests")]
    [Arguments("tests/Heartbeat.Model.Tests/JsonAmbiguityTests.cs", "dotnet-build,model-tests")]
    [Arguments("tools/Heartbeat.Dev/DeveloperCli.cs", "dotnet-build,developer-cli-tests")]
    [Arguments("src/Observers/Heartbeat.Observer.ForegroundState/Program.cs", "dotnet-build,integration-tests")]
    [Arguments("tools/Heartbeat.AppHost/Program.cs", "dotnet-build")]
    [Arguments("src/Frontend/Heartbeat.Web/src/main.tsx", "frontend-verify")]
    [Arguments("unclassified.config", "dotnet-build,model-tests,integration-tests,developer-cli-tests,frontend-verify,docs-check,docs-test,docs-types,docs-build")]
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

        public Task<ProcessResult> CaptureAsync(string fileName, IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string?>? environment, CancellationToken cancellationToken)
            => Task.FromResult(new ProcessResult(0,
                fileName == "git" && arguments[0] == "diff" && ChangedPath is not null ? ChangedPath + '\0' : "", ""));
    }
}
