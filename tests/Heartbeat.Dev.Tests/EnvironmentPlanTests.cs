using Heartbeat.Dev;

namespace Heartbeat.Dev.Tests;

public sealed class EnvironmentPlanTests
{
    [Fact]
    public void WebExpandsOnlyItsRequiredDependencies()
    {
        var plan = EnvironmentPlan.Parse(["up", "web"]);

        Assert.Equal(["db", "migrate", "api", "web"], plan.ComposeServices);
        Assert.False(plan.RunDesktop);
    }

    [Fact]
    public void DesktopStartsHubAndRemainsForeground()
    {
        var plan = EnvironmentPlan.Parse(["up", "desktop"]);

        Assert.Equal(["hub"], plan.ComposeServices);
        Assert.True(plan.RunDesktop);
    }

    [Fact]
    public void DefaultSelectionIsWebApiAndDatabase()
    {
        var plan = EnvironmentPlan.Parse(["status"]);

        Assert.Equal(["web", "api", "db"], plan.ComposeServices);
    }

    [Fact]
    public void ResetRequiresExplicitApplyToBeDestructive()
    {
        var preview = EnvironmentPlan.Parse(["reset"]);
        var apply = EnvironmentPlan.Parse(["reset", "--apply"]);

        Assert.False(preview.Options.Apply);
        Assert.True(apply.Options.Apply);
    }

    [Theory]
    [InlineData("logs")]
    [InlineData("status")]
    [InlineData("down")]
    public void DesktopCannotBeManagedAsAContainer(string action)
    {
        Assert.Throws<CommandUsageException>(() => EnvironmentPlan.Parse([action, "desktop"]));
    }

    [Theory]
    [InlineData("status", "--release")]
    [InlineData("up", "--json")]
    [InlineData("up", "--apply")]
    [InlineData("reset", "web")]
    [InlineData("up", "unknown")]
    public void RejectsOptionsThatDoNotBelongToTheAction(string action, string option)
    {
        Assert.Throws<CommandUsageException>(() => EnvironmentPlan.Parse([action, option]));
    }
}
