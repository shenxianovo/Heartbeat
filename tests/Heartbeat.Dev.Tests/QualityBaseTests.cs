using Heartbeat.Dev;

namespace Heartbeat.Dev.Tests;

/// 选错基点的代价是一整套 0，所以「基点能不能用」本身要先被判断出来。
public sealed class QualityBaseTests
{
    [Fact]
    public void AnchorAliasResolvesToTheRewriteAnchorAndOtherRefsAreLeftAlone()
    {
        Assert.Equal(RewriteLineage.AnchorCommit, RewriteLineage.Resolve("anchor"));
        Assert.Equal("HEAD", RewriteLineage.Resolve("HEAD"));
        Assert.Equal("main", RewriteLineage.Resolve("main"));
    }

    [Fact]
    public void ABaseWithProductionCodeIsUsable()
    {
        var baseline = new SourceSnapshot("abc1234", [new("src/A.cs", "C#", SourceRole.Production, 10)]);

        var usability = BaseUsability.Evaluate("HEAD", baseline);

        Assert.True(usability.Usable);
        Assert.Null(usability.Reason);
        Assert.Empty(usability.Suggestions);
    }

    /// 重写前的 main 就是这种树：源码都在，但一行都不在 src/ 下。报告必须说这个，而不是报 0。
    [Fact]
    public void ABaseWithoutProductionCodeIsRejectedWithTheLayoutItActuallyHas()
    {
        var baseline = new SourceSnapshot("0f1e2d3c4b5a", [
            new("collection/agent.py", "Python", SourceRole.Unclassified, 400),
            new("server/app.py", "Python", SourceRole.Unclassified, 900),
            new("frontend/page.tsx", "TypeScript", SourceRole.Unclassified, 300),
            new("README.md", "Markdown", SourceRole.Documentation, 40),
        ]);

        var usability = BaseUsability.Evaluate("main", baseline);

        Assert.False(usability.Usable);
        Assert.Contains("'main' (0f1e2d3)", usability.Reason, StringComparison.Ordinal);
        Assert.Contains("0 production files under src/", usability.Reason, StringComparison.Ordinal);
        Assert.Contains("wrong base", usability.Reason, StringComparison.Ordinal);
        Assert.Contains(
            usability.Suggestions,
            suggestion => suggestion.Contains("collection/, frontend/, server/", StringComparison.Ordinal));
        Assert.Contains(
            usability.Suggestions,
            suggestion => suggestion.Contains(RewriteLineage.StockCommand(), StringComparison.Ordinal));
        Assert.Contains(
            usability.Suggestions,
            suggestion => suggestion.Contains("--base HEAD", StringComparison.Ordinal));
    }
}

/// 存量上限是绝对值，和基点无关：已提交的热点不能靠换基点消失。
public sealed class StockBudgetTests
{
    private const string Path = "/repo/tools/Heartbeat.Dev/quality-budget.json";

    [Fact]
    public void CountAtOrBelowTheCommittedLimitPasses()
    {
        var report = StockBudget.Evaluate(Path, """{ "maxProductionComplexityHotspots": 7 }""", currentHotspots: 7);

        Assert.True(report.Available);
        Assert.True(report.Passed);
        Assert.Equal(7, report.Limit);
        Assert.Equal("tools/Heartbeat.Dev/quality-budget.json", report.Path);
        Assert.Null(report.Reason);
    }

    [Fact]
    public void CountAboveTheCommittedLimitFailsAndSaysWhereToChangeIt()
    {
        var report = StockBudget.Evaluate(Path, """{ "maxProductionComplexityHotspots": 7 }""", currentHotspots: 9);

        Assert.True(report.Available);
        Assert.False(report.Passed);
        Assert.Contains("9 production functions", report.Reason!, StringComparison.Ordinal);
        Assert.Contains("the committed stock budget is 7", report.Reason!, StringComparison.Ordinal);
        Assert.Contains("tools/Heartbeat.Dev/quality-budget.json", report.Reason!, StringComparison.Ordinal);
    }

    /// 预算文件不存在不等于没有上限：那样闸门就永远绿。
    [Fact]
    public void AMissingBudgetIsAFailureNotAnAbsentCheck()
    {
        var report = StockBudget.Evaluate(Path, null, currentHotspots: 4);

        Assert.False(report.Available);
        Assert.False(report.Passed);
        Assert.Equal(4, report.Current);
        Assert.Contains("missing", report.Reason!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("""{ "maxProductionComplexityHotspots": -1 }""")]
    public void AnUnreadableBudgetIsAlsoAFailure(string json)
    {
        var report = StockBudget.Evaluate(Path, json, currentHotspots: 4);

        Assert.False(report.Available);
        Assert.False(report.Passed);
        Assert.NotNull(report.Reason);
    }
}
