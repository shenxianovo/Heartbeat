using Heartbeat.Dev;

namespace Heartbeat.Dev.Tests;

public sealed class DeveloperCliTests
{
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
