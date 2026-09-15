using Heartbeat.Dev;

namespace Heartbeat.Dev.Tests;

public sealed class ScenarioOptionsTests
{
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
}
