using Heartbeat.Dev;

namespace Heartbeat.Dev.Tests;

public sealed class DesktopEnvironmentTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public async Task DesktopBuildsTheApplicationAndOpensItOnlyAfterSuccessfulPackaging(int packageExit)
    {
        if (!OperatingSystem.IsMacOS()) return;
        var directory = Directory.CreateTempSubdirectory("heartbeat-dev-desktop-").FullName;
        try
        {
            var runner = new DesktopRunner(packageExit);
            var command = new EnvironmentCommand(new RepositoryContext(directory), runner, TextWriter.Null, TextWriter.Null);

            var result = await command.RunAsync(["up", "desktop"], CancellationToken.None);

            Assert.Equal(packageExit, result);
            Assert.Equal("/bin/bash", runner.Calls[0].File);
            Assert.Equal(Path.Combine(directory, "scripts", "package-desktop-mac.sh"), Assert.Single(runner.Calls[0].Args));
            if (packageExit != 0) Assert.Single(runner.Calls);
            else
            {
                Assert.Equal(2, runner.Calls.Count);
                Assert.Equal("/usr/bin/open", runner.Calls[1].File);
                Assert.Equal(["-a", Path.Combine(directory, ".artifacts", "desktop", "Heartbeat Dev.app")], runner.Calls[1].Args);
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private sealed class DesktopRunner(int packageExit) : IProcessRunner
    {
        public List<(string File, IReadOnlyList<string> Args)> Calls { get; } = [];
        public Task<ProcessResult> CaptureAsync(string fileName, IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string?>? environment, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A desktop-only launch must not invoke Docker or validate Hub environment variables.");

        public Task<int> RunAsync(string fileName, IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string?>? environment, CancellationToken cancellationToken)
        {
            Calls.Add((fileName, arguments));
            return Task.FromResult(fileName == "/bin/bash" ? packageExit : 0);
        }
    }
}
