using Heartbeat.Dev;

namespace Heartbeat.Dev.Tests;

public sealed class EnvironmentPlanTests
{
    [Fact]
    public void WebExpandsOnlyItsRequiredDependencies()
    {
        var plan = Plan(EnvironmentAction.Up, "web");

        Assert.Equal(["db", "migrate", "api", "web"], plan.ComposeServices);
        Assert.False(plan.RunDesktop);
    }

    [Fact]
    public void DesktopUsesItsOwnHubWithoutStartingContainers()
    {
        var plan = Plan(EnvironmentAction.Up, "desktop");

        Assert.Empty(plan.ComposeServices);
        Assert.True(plan.RunDesktop);
    }

    [Fact]
    public void DefaultSelectionIsWebApiAndDatabase()
    {
        var plan = Plan(EnvironmentAction.Status);

        Assert.Equal(["web", "api", "db"], plan.ComposeServices);
    }

    [Fact]
    public void ResetRequiresExplicitApplyToBeDestructive()
    {
        var preview = Plan(EnvironmentAction.Reset);
        var apply = EnvironmentPlan.Create(new EnvironmentOptions(EnvironmentAction.Reset, false, null, false, true, new HashSet<string>()));

        Assert.False(preview.Options.Apply);
        Assert.True(apply.Options.Apply);
    }

    [Theory]
    [InlineData("logs")]
    [InlineData("status")]
    [InlineData("down")]
    public void DesktopCannotBeManagedAsAContainer(string action)
    {
        Assert.Throws<CommandUsageException>(() => Plan(Enum.Parse<EnvironmentAction>(action, ignoreCase: true), "desktop"));
    }

    private static EnvironmentPlan Plan(EnvironmentAction action, params string[] services) =>
        EnvironmentPlan.Create(new EnvironmentOptions(action, false, null, false, false, new HashSet<string>(services)));
}
