using Heartbeat.Dev;

namespace Heartbeat.Dev.Tests;

public sealed class ComplexityDetectorTests
{
    [Fact]
    public void ParsesProductionCSharpDiagnosticsAndDeduplicatesBuildOutput()
    {
        const string root = "/repo";
        const string diagnostic = "/repo/src/App/Worker.cs(12,8): warning CA1502: 'Run' has a cyclomatic complexity of '14'. Rewrite it.";

        var hotspots = ComplexityDetector.ParseCSharp(root, $"{diagnostic}\n{diagnostic}\n/repo/tests/App.Tests/X.cs(1,1): warning CA1502: 'Test' has a cyclomatic complexity of '20'.");

        var hotspot = Assert.Single(hotspots);
        Assert.Equal(new ComplexityHotspot("C#", "src/App/Worker.cs", 12, "Run", 14), hotspot);
    }

    [Fact]
    public void FlagsOnlyNewOrGrowingHotspots()
    {
        var baseline = new[]
        {
            new ComplexityHotspot("C#", "src/A.cs", 10, "Stable", 12),
            new ComplexityHotspot("C#", "src/A.cs", 20, "Growing", 11),
        };
        var current = new[]
        {
            new ComplexityHotspot("C#", "src/A.cs", 12, "Stable", 12),
            new ComplexityHotspot("C#", "src/A.cs", 22, "Growing", 13),
            new ComplexityHotspot("C#", "src/B.cs", 4, "New", 11),
        };

        var regressions = ComplexityDetector.FindRegressions(baseline, current);

        Assert.Collection(regressions,
            growing => Assert.Equal(11, growing.BaseComplexity),
            added => Assert.Null(added.BaseComplexity));
    }

    [Fact]
    public void CalculatesComplexityWeightedErosionAcrossAllFunctions()
    {
        var functions = new[]
        {
            new FunctionMetric("C#", "src/A.cs", 1, "Simple", 2, 5),
            new FunctionMetric("TypeScript", "src/a.ts", 10, "Risky", 12, 10),
        };

        var erosion = ComplexityDetector.CalculateErosion(functions);

        Assert.Equal(2, erosion.TotalFunctions);
        Assert.Equal(1, erosion.HighRiskFunctions);
        Assert.Equal(130, erosion.TotalBurden);
        Assert.Equal(120, erosion.HighRiskBurden);
        Assert.Equal(120d / 130d, erosion.Ratio, 10);
    }

    [Fact]
    public void UsesAnalyzerComplexityForMatchingFunctionBurden()
    {
        var functions = new[] { new FunctionMetric("C#", "src/A.cs", 10, "Run", 3, 20) };
        var hotspots = new[] { new ComplexityHotspot("C#", "src/A.cs", 10, "Run", 14) };

        var merged = ComplexityDetector.ApplyAnalyzerComplexity(functions, hotspots);

        Assert.Equal(14, Assert.Single(merged).Complexity);
    }
}
