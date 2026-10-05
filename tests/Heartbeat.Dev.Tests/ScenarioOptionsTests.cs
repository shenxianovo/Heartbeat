using Heartbeat.Dev;

namespace Heartbeat.Dev.Tests;

public sealed class ScenarioOptionsTests
{
    [Fact]
    public async Task ListsAvailableScenarios()
    {
        using var output = new StringWriter();
        var cli = new DeveloperCli(new RepositoryContext(Path.GetTempPath()), null!, output, TextWriter.Null);
        Assert.Equal(0, await cli.RunAsync(["scenario", "--list"], CancellationToken.None));
        Assert.Equal(["replay-fixture", "hubs-fixture", "delivery", "collector-delivery", "runtime-replay", "desktop-replay", "native-desktop"],
            output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
    }
}
