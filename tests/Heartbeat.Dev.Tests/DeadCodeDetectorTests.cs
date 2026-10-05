using Heartbeat.Dev;

namespace Heartbeat.Dev.Tests;

public sealed class DeadCodeDetectorTests
{
    [Fact]
    public void KnipFilesAndExportsGateNewFindingsButNotLineMoves()
    {
        var findings = DeadCodeDetector.ParseKnip("""
            {"issues":[
              {"file":"src/old.ts","files":[{"name":"src/old.ts"}],"exports":[]},
              {"file":"src/shared.ts","exports":[{"name":"unused","line":7}],"types":[]}
            ]}
            """);
        var baseline = new[] { findings[1] with { Line = 1 } };
        var added = DeadCodeDetector.FindNew(baseline, findings);
        Assert.Equal("knip/files", Assert.Single(added).Rule);
        var report = new DeadCodeReport(true, null, findings, added);
        Assert.Single(report.Failures(stock: false));
        Assert.Empty(report.Failures(stock: true));
    }

    [Fact]
    public void MissingOrMalformedScansCannotLookClean()
    {
        Assert.Throws<InvalidDataException>(() => DeadCodeDetector.ParseKnip("{}"));
        var report = new DeadCodeReport(false, "scanner missing", [], []);
        Assert.Equal("scanner missing", Assert.Single(report.Failures(stock: true)));
        Assert.Equal("scanner missing", Assert.Single(report.Failures(stock: false)));
    }

    [Fact]
    public void RoslynDiagnosticsKeepProductionMembersOnceAndIgnoreTestHelpers()
    {
        var root = Path.GetTempPath();
        var production = Path.Combine(root, "src", "Backend", "Unused.cs");
        var test = Path.Combine(root, "tests", "Fixture.cs");
        var line = $"{production}(12,8): warning IDE0051: Private member 'Unused.Read' is unused [project.csproj]";
        var findings = DeadCodeDetector.ParseCSharp(root, $"{line}\n{line}\n{test}(3,2): warning IDE0051: Private member 'Fixture.Read' is unused");
        var finding = Assert.Single(findings);
        Assert.Equal("src/Backend/Unused.cs", finding.Path);
        Assert.Equal("Unused.Read", finding.Symbol);
    }
}
