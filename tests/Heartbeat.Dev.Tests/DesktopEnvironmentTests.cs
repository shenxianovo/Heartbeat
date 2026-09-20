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
            var runner = new PackageTestRunner(packageExit == 0 ? null : "dotnet");
            var command = new DeveloperCli(new RepositoryContext(directory), runner, TextWriter.Null, TextWriter.Null);
            var result = await command.RunAsync(["env", "up", "desktop"], CancellationToken.None);

            Assert.Equal(packageExit, result);
            Assert.Equal("dotnet", runner.Calls[0].File);
            Assert.DoesNotContain(runner.Calls, call => call.File == "docker");
            if (packageExit != 0) Assert.Single(runner.Calls);
            else
            {
                Assert.Equal("/usr/bin/open", runner.Calls[^1].File);
                Assert.Equal(["-a", Path.Combine(directory, ".artifacts", "desktop", "Heartbeat Dev.app")], runner.Calls[^1].Args);
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
