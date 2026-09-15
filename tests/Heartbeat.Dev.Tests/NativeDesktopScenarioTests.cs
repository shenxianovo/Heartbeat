using Heartbeat.Dev;

namespace Heartbeat.Dev.Tests;

public sealed class NativeDesktopScenarioTests
{
    [Theory]
    [InlineData("{\"pending\":0,\"failed\":0}", "{\"pending\":1,\"failed\":0}", true)]
    [InlineData("{\"pending\":2,\"failed\":1}", "{\"pending\":2,\"failed\":1}", false)]
    public void RequiresNewHubQueueEvidence(string before, string after, bool expected)
    {
        Assert.Equal(expected, NativeDesktopScenario.HasNewQueueEvidence(before, after));
    }

    [Fact]
    public async Task StopsDotnetRunChildAsSuccessfulUserCancellation()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var repository = await RepositoryContext.DiscoverAsync(Environment.CurrentDirectory);
        var assembly = repository.Path(
            "tests", "Heartbeat.Dev.Tests", "Fixtures", "SignalAwareProcess", "bin", "Debug", "net10.0", "SignalAwareProcess.dll");
        using var process = NativeDesktopScenario.StartManagedProcess(assembly, repository.Root, null);
        Assert.Equal("ready", await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20)));

        await NativeDesktopScenario.StopCollectorAsync(
            process, TimeSpan.FromSeconds(2), CancellationToken.None);

        Assert.Equal(0, process.ExitCode);
    }
}
