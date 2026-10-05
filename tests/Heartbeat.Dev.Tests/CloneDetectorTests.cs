using Heartbeat.Dev;

namespace Heartbeat.Dev.Tests;

/// <summary>
/// jscpd 是闸门，不是观察项。这里钉住两件事：报告里的重复被读成可行动的位置，
/// 以及「扫不了」和「有新增重复」是两种不同的结果，不能混成同一句话。
/// </summary>
public sealed class CloneDetectorTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"heartbeat-jscpd-{Guid.NewGuid():N}");

    private const string Report = """
        {
          "duplicates": [
            {
              "format": "csharp",
              "kind": "exact",
              "lines": 9,
              "tokens": 84,
              "isNew": true,
              "firstFile": { "name": "Backend/Heartbeat.Api/Endpoints/RecordEndpoints.cs", "start": 202, "end": 210 },
              "secondFile": { "name": "Backend/Heartbeat.Api/Endpoints/TrackEndpoints.cs", "start": 73, "end": 81 }
            },
            {
              "format": "csharp",
              "kind": "exact",
              "lines": 12,
              "tokens": 96,
              "isNew": false,
              "firstFile": { "name": "src/Backend/Heartbeat.Domain/Recording/Record.cs", "start": 10, "end": 21 },
              "secondFile": { "name": "Backend/Heartbeat.Domain/Recording/Track.cs", "start": 30, "end": 41 }
            }
          ],
          "statistics": {
            "total": {
              "clones": 2,
              "newClones": 1,
              "duplicatedLines": 21,
              "percentage": 1.0564952135310748,
              "lines": 20161
            }
          }
        }
        """;

    [Fact]
    public void FindingsCarryBothLocationsTheirSizeAndWhetherTheyAreNew()
    {
        var scan = CloneDetector.Parse("production", "src", null, Report);

        Assert.True(scan.Available);
        Assert.Equal(2, scan.Clones);
        Assert.Equal(1, scan.NewClones);
        Assert.Equal(21, scan.DuplicatedLines);
        Assert.Equal(1.06, scan.DuplicatedLinePercentage, 2);
        var added = Assert.Single(scan.NewFindings);
        Assert.Equal(
            "src/Backend/Heartbeat.Api/Endpoints/RecordEndpoints.cs:202-210 <-> "
            + "src/Backend/Heartbeat.Api/Endpoints/TrackEndpoints.cs:73-81 (9 lines, 84 tokens, new since base)",
            added.Describe());
        var stock = Assert.Single(scan.StockFindings);
        Assert.EndsWith("(12 lines, 96 tokens, stock)", stock.Describe(), StringComparison.Ordinal);
        // 报告里的路径已经带前缀时不再补一次。
        Assert.Equal("src/Backend/Heartbeat.Domain/Recording/Record.cs", stock.FirstFile);
    }

    [Fact]
    public void ReportOnDiskIsReadEvenThoughJscpdExitsNonZeroForNewClones()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "jscpd-report.json"), Report);

        var scan = CloneDetector.Read("tests", "tests", _directory, new ProcessResult(1, "time: 437ms", string.Empty));

        Assert.True(scan.Available);
        Assert.Equal(1, scan.NewClones);
    }

    /// 以前失败原因是「输出的最后一行」，也就是 `time: 437ms`。扫不了要说扫不了，并给出那条错误。
    [Fact]
    public void AMissingReportIsUnavailableAndQuotesTheErrorRatherThanTheTiming()
    {
        Directory.CreateDirectory(_directory);

        var scan = CloneDetector.Read(
            "production",
            "src",
            _directory,
            new ProcessResult(1, "ERROR: Unknown option --baseline-from-ref\ntime: 437ms", string.Empty));

        Assert.False(scan.Available);
        Assert.Contains("could not scan production", scan.Unavailable!, StringComparison.Ordinal);
        Assert.Contains("Unknown option --baseline-from-ref", scan.Unavailable!, StringComparison.Ordinal);
        Assert.DoesNotContain("437ms", scan.Unavailable!, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}

/// <summary>
/// 谁挂谁不挂：gate 模式拦相对基点的新增，stock 模式只报账；两种模式都拦「量不出来」和「存量上限被突破」。
/// </summary>
public sealed class QualityGateTests
{
    [Fact]
    public void GateFailsOnNewClonesAndNamesTheDuplicatedRanges()
    {
        var failures = QualityCommand.Failures(
            stock: false,
            new CloneQualityReport(
                Scan("production", newClones: 1, [Finding("src/A.cs", "src/B.cs")]),
                Scan("tests", newClones: 0, [])),
            Healthy(),
            Budget(passed: true));

        var failure = Assert.Single(failures);
        Assert.Contains("1 new production clone cluster(s)", failure, StringComparison.Ordinal);
        Assert.Contains("src/A.cs:10-18 <-> src/B.cs:40-48", failure, StringComparison.Ordinal);
    }

    /// 测试之间的重复也是重复：一个类型改名要在八个地方跟着改，就是它带来的。
    [Fact]
    public void GateFailsOnNewClonesInTestsToo()
    {
        var failures = QualityCommand.Failures(
            stock: false,
            new CloneQualityReport(
                Scan("production", newClones: 0, []),
                Scan("tests", newClones: 2, [Finding("tests/A.cs", "tests/B.cs")])),
            Healthy(),
            Budget(passed: true));

        Assert.Contains("2 new tests clone cluster(s)", Assert.Single(failures), StringComparison.Ordinal);
    }

    [Fact]
    public void StockModeReportsAccumulatedDuplicationWithoutFailing()
    {
        var failures = QualityCommand.Failures(
            stock: true,
            new CloneQualityReport(
                Scan("production", newClones: 7, [Finding("src/A.cs", "src/B.cs")]),
                Scan("tests", newClones: 18, [Finding("tests/A.cs", "tests/B.cs")])),
            Complexity(hotspots: 12, regressions: 5),
            Budget(passed: true));

        Assert.Empty(failures);
    }

    [Fact]
    public void AScanThatCouldNotRunFailsInBothModes()
    {
        var unavailable = new CloneQualityReport(
            new CloneScan("production", false, "Run npm --prefix tools/Heartbeat.Dev/jscpd ci --ignore-scripts",
                null, 0, 0, 0, 0, [], []),
            Scan("tests", newClones: 0, []));

        foreach (var stock in new[] { false, true })
        {
            var failures = QualityCommand.Failures(stock, unavailable, Healthy(), Budget(passed: true));
            Assert.Contains("Run npm --prefix", Assert.Single(failures), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AComplexityScanWithoutProductionMetricsFailsInsteadOfReportingZero()
    {
        var complexity = new ComplexityQualityReport(
            Available: false,
            Passed: false,
            Reason: "CA1502 produced no production function metrics; the base predates the current layout.",
            CurrentHotspots: 0,
            Regressions: 0,
            ReportPath: null);

        var failures = QualityCommand.Failures(stock: true, Clean(), complexity, null);

        Assert.Contains("no production function metrics", Assert.Single(failures), StringComparison.Ordinal);
    }

    [Fact]
    public void ExceedingTheStockBudgetFailsEvenInStockMode()
    {
        var failures = QualityCommand.Failures(stock: true, Clean(), Healthy(), Budget(passed: false));

        Assert.Contains("stock budget", Assert.Single(failures), StringComparison.Ordinal);
    }

    [Fact]
    public void GateFailsWhenAProductionFunctionCrossesTheComplexityLine()
    {
        var regression = new ComplexityRegression(
            new ComplexityHotspot("C#", "src/App/Worker.cs", 12, "Run", 14), null);
        var complexity = Complexity(hotspots: 1, regressions: 1) with { RegressionDetails = [regression] };

        var failure = Assert.Single(QualityCommand.Failures(stock: false, Clean(), complexity, Budget(passed: true)));

        Assert.Contains("src/App/Worker.cs:12 Run complexity absent at base -> 14", failure, StringComparison.Ordinal);
    }

    private static CloneQualityReport Clean() =>
        new(Scan("production", newClones: 0, []), Scan("tests", newClones: 0, []));

    private static CloneScan Scan(string scope, int newClones, IReadOnlyList<CloneFinding> findings) =>
        new(scope, true, null, "/tmp/report", findings.Count, newClones, 21, 1.05, findings, []);

    private static CloneFinding Finding(string first, string second) =>
        new(first, 10, 18, second, 40, 48, 9, 84, IsNew: true);

    private static ComplexityQualityReport Healthy() => Complexity(hotspots: 3, regressions: 0);

    private static ComplexityQualityReport Complexity(int hotspots, int regressions)
    {
        var erosion = new ErosionMeasurement(10, hotspots, 200, 60, 0.3);
        return new ComplexityQualityReport(
            Available: true,
            Passed: regressions == 0,
            Reason: null,
            CurrentHotspots: hotspots,
            Regressions: regressions,
            ReportPath: null,
            BaselineErosion: erosion,
            CurrentErosion: erosion,
            ErosionDelta: 0,
            RegressionDetails: []);
    }

    private static StockBudgetReport Budget(bool passed) => passed
        ? new StockBudgetReport(true, true, "tools/Heartbeat.Dev/quality-budget.json", 7, 7, null)
        : new StockBudgetReport(true, false, "tools/Heartbeat.Dev/quality-budget.json", 7, 9,
            "9 production functions are above complexity 10; the committed stock budget is 7.");
}
