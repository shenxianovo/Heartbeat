using Heartbeat.Dev;

namespace Heartbeat.Dev.Tests;

public sealed class CouplingParserTests
{
    [Fact]
    public void KeepsOnlyProductionCouplingWarningsAndDeduplicatesBuildSummary()
    {
        const string diagnostic = "/repo/src/App.cs(1,1): warning CA1506: '<Main>$' is coupled with '73' different types from '30' different namespaces.";
        var result = CouplingParser.Parse("/repo", diagnostic + "\n" + diagnostic + "\n"
            + diagnostic.Replace("/src/", "/tests/", StringComparison.Ordinal));
        Assert.Equal(new CouplingHotspot("src/App.cs", 1, "<Main>$", 73), Assert.Single(result));
    }
}
