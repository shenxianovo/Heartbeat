using Heartbeat.Dev;

namespace Heartbeat.Dev.Tests;

public sealed class DesktopEnvironmentTests
{
    [Theory]
    [InlineData("win-x64", 0)]
    [InlineData("win-arm64", 0)]
    [InlineData("win-x64", 7)]
    public async Task WindowsDesktopUsesTheSharedPackageCommandAndLaunchesWithoutWaiting(string runtime, int packageExit)
    {
        var directory = Directory.CreateTempSubdirectory("heartbeat-windows-launch-").FullName;
        try
        {
            var runner = new PackageTestRunner(packageExit == 0 ? null : "dotnet");
            var command = new EnvironmentCommand(new RepositoryContext(directory), runner, TextWriter.Null, TextWriter.Null, runtime);
            var plan = EnvironmentPlan.Create(new EnvironmentOptions(EnvironmentAction.Up, false, null, false, false,
                new HashSet<string> { "desktop" }));
            Assert.Equal(packageExit, await command.RunAsync(plan, CancellationToken.None));
            Assert.Equal("dotnet", Assert.Single(runner.Calls).File);
            Assert.Contains(runtime, runner.Calls[0].Args);
            if (packageExit == 0)
                Assert.Equal(Path.Combine(directory, ".artifacts", "desktop-windows", "Heartbeat Dev", "Heartbeat.Desktop.Windows.exe"), runner.OpenedApplication);
            else Assert.Null(runner.OpenedApplication);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("setup")]
    [InlineData("status")]
    public async Task WindowsSigningCommandsExplainThatNoIdentityIsNeeded(string action)
    {
        var runner = new PackageTestRunner();
        using var output = new StringWriter();
        var command = new SigningCommand(runner, output, "win-x64").CreateCommand();
        Assert.Equal(0, await command.Parse([action]).InvokeAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("not required", output.ToString());
        Assert.Empty(runner.Calls);
    }

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
            Assert.Contains(runner.Calls, call => call.File == "dotnet");
            Assert.DoesNotContain(runner.Calls, call => call.File == "docker");
            if (packageExit != 0) Assert.DoesNotContain(runner.Calls, call => call.File == "/usr/bin/open");
            else
            {
                Assert.Equal("/usr/bin/open", runner.Calls[^1].File);
                Assert.Equal(["-a", Path.Combine(directory, ".artifacts", "desktop", "Heartbeat Dev.app")], runner.Calls[^1].Args);
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
