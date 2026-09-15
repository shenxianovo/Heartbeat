using Heartbeat.Dev;

namespace Heartbeat.Dev.Tests;

public sealed class TypeScriptMetricsTests : IDisposable
{
    private readonly string _root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), $"heartbeat-ts-metrics-{Guid.NewGuid():N}");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MatchesAnonymousFunctionsOnTheSameLineAndSurvivesLineShifts(bool properties)
    {
        var repository = await RepositoryContext.DiscoverAsync(Environment.CurrentDirectory);
        var web = repository.Path("src", "Frontend", "Heartbeat.Web");
        var directory = Path.Combine(_root, "src", "Frontend", "Heartbeat.Web", "src");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "fixture.js");
        var first = (properties ? "const holder = { first: x => " : "const first = x => ")
            + string.Join(" || ", Enumerable.Repeat("x", 12)) + (properties ? "," : ";");
        var second = (properties ? "second: x => " : "const second = x => ")
            + string.Join(" && ", Enumerable.Repeat("x", 13)) + (properties ? "};" : ";");
        File.WriteAllText(file, first + second);

        var before = await MeasureAsync(repository, web, file);
        Assert.Equal([12, 13], before.Select(item => item.Complexity));
        Assert.Equal(2, before.Select(item => item.Symbol).Distinct().Count());
        File.WriteAllText(file, "// added comment\n\n" + first
            + second.Replace(properties ? "x};" : "x;", properties ? "x && x};" : "x && x;", StringComparison.Ordinal));
        var after = await MeasureAsync(repository, web, file);
        var regression = Assert.Single(ComplexityDetector.FindRegressions(
            before.Select(item => item.Hotspot).ToArray(), after.Select(item => item.Hotspot).ToArray()));
        Assert.Equal(13, regression.BaseComplexity);
        Assert.Equal(14, regression.Current.Complexity);
    }

    [Fact]
    public async Task MeasuresClassInitializersAndStaticBlocksAlongsideArrowFunctions()
    {
        var repository = await RepositoryContext.DiscoverAsync(Environment.CurrentDirectory);
        var web = repository.Path("src", "Frontend", "Heartbeat.Web");
        var directory = Path.Combine(_root, "src", "Frontend", "Heartbeat.Web", "src");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "fixture.js");
        File.WriteAllText(file, "class A { value = flag || 1; run = x => x || 1; static { if (flag) work(); } }");

        var metrics = await MeasureAsync(repository, web, file, 4);

        Assert.Equal(2, Assert.Single(metrics, item => item.Kind == "function").Complexity);
        Assert.Equal(2, Assert.Single(metrics, item => item.Kind == "static-block").Complexity);
        Assert.Equal([1, 2], metrics.Where(item => item.Kind == "initializer").Select(item => item.Complexity).Order());
    }

    private async Task<IReadOnlyList<FunctionMetric>> MeasureAsync(RepositoryContext repository, string web, string file, int count = 2)
    {
        var runner = new ProcessRunner(_root);
        var spans = await runner.CaptureAsync("node",
            [repository.Path("tools", "Heartbeat.Dev", "erosion-typescript.mjs"), _root, web], null, CancellationToken.None);
        Assert.True(spans.ExitCode == 0, spans.StdErr);
        var eslint = await runner.CaptureAsync("node",
            [Path.Combine(web, "node_modules", "eslint", "bin", "eslint.js"), file,
                "--no-config-lookup", "--rule", "complexity: [warn, 0]", "--format", "json"], null, CancellationToken.None);
        Assert.True(eslint.ExitCode == 0, eslint.StdErr);
        Assert.True(ComplexityDetector.ParseTypeScript(_root, eslint.StdOut).Count == count, eslint.StdOut);
        return ComplexityDetector.ApplyAnalyzerComplexity(ErosionScanner.ParseTypeScriptSpans(spans.StdOut),
            ComplexityDetector.ParseTypeScript(_root, eslint.StdOut));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
