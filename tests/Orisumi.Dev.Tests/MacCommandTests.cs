using System.Text.Json;
using Orisumi.Dev;
using TUnit.Core.Enums;

namespace Orisumi.Dev.Tests;

public sealed class MacCommandTests
{
    [Test, RunOn(OS.MacOs)]
    [Arguments("debug", "Debug")]
    [Arguments("release", "Release")]
    [Arguments(null, "Release")]
    public async Task BuildAndRunUseTheSameSelectedConfiguration(string? selected, string expected)
    {
        using var fixture = new EnvironmentFixture();
        var runner = new MacRunner(fixture.Repository);
        var application = new MacCommand(fixture.Repository, runner, fixture.Output).Application(expected);
        Directory.CreateDirectory(application);
        var cli = new DeveloperCli(fixture.Repository, runner, fixture.Output, TextWriter.Null);
        string[] configuration = selected is null ? [] : ["--configuration", selected];
        await Assert.That(await cli.RunAsync(["mac", "build", .. configuration], CancellationToken.None)).IsEqualTo(0);
        await Assert.That(await cli.RunAsync(["mac", "run", .. configuration, "--build", "--window"], CancellationToken.None)).IsEqualTo(0);
        var builds = runner.Calls.Where(call => call.File == "xcodebuild").ToArray();
        await Assert.That(builds.Length).IsEqualTo(2);
        foreach (var build in builds)
            await Assert.That(build.Args[Array.IndexOf(build.Args, "-configuration") + 1]).IsEqualTo(expected);
        var launched = runner.Calls.Single(call => call.File == "open");
        await Assert.That(launched.Args[1]).IsEqualTo(application);
        await Assert.That(launched.Args.Contains("-showWindow")).IsTrue();
    }

    private sealed class MacRunner(RepositoryContext repository) : IProcessRunner
    {
        public List<(string File, string[] Args)> Calls { get; } = [];
        public Task<ProcessResult> CaptureAsync(string fileName, IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string?>? environment, CancellationToken cancellationToken)
        {
            Calls.Add((fileName, arguments.ToArray()));
            var stdout = "";
            if (fileName == "aspire" && arguments[0] == "ps")
                stdout = JsonSerializer.Serialize(new[] { new
                {
                    appHostPath = repository.Path("tools", "Orisumi.AppHost", "Orisumi.AppHost.csproj"),
                    status = "running",
                } });
            if (fileName == "aspire" && arguments[0] == "describe")
                stdout = """{"resources":[{"name":"api","resourceType":"Project","state":"Running"}]}""";
            return Task.FromResult(new ProcessResult(0, stdout, ""));
        }
    }
}
