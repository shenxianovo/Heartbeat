using Heartbeat.Dev;

namespace Heartbeat.Dev.Tests;

public sealed class ScenarioOptionsTests
{
    [Fact]
    public async Task ListsAvailableScenarios()
    {
        using var output = new StringWriter();
        var command = new ScenarioCommand(new RepositoryContext(Path.GetTempPath()),
            null!, output);

        Assert.Equal(0, await command.RunAsync(["--list"], CancellationToken.None));
        Assert.Equal(["replay-fixture", "delivery", "collector-delivery", "collector-replay", "native-desktop"],
            output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void SensitiveEvidenceAndFailureRetentionRequireExplicitFlags()
    {
        var defaults = ScenarioOptions.Parse(["native-desktop"]);
        var optedIn = ScenarioOptions.Parse([
            "native-desktop", "--include-sensitive-evidence", "--keep-environment-on-failure",
        ]);

        Assert.False(defaults.IncludeSensitiveEvidence);
        Assert.False(defaults.KeepEnvironmentOnFailure);
        Assert.True(optedIn.IncludeSensitiveEvidence);
        Assert.True(optedIn.KeepEnvironmentOnFailure);
    }

    [Fact]
    public void RejectsNativeOnlyRetentionForAutomatedScenario()
    {
        var error = Assert.Throws<CommandUsageException>(() =>
            ScenarioOptions.Parse(["replay-fixture", "--keep-environment-on-failure"]));

        Assert.Contains("native-desktop", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("collector-delivery")]
    [InlineData("collector-replay")]
    public void CollectorScenariosCanRetainTheirFailedEnvironment(string scenario)
    {
        var options = ScenarioOptions.Parse([scenario, "--keep-environment-on-failure"]);

        Assert.True(options.KeepEnvironmentOnFailure);
        Assert.False(options.IncludeSensitiveEvidence);
    }
}
