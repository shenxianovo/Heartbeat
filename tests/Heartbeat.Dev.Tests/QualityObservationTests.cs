using Heartbeat.Dev;

namespace Heartbeat.Dev.Tests;

public sealed class QualityObservationTests
{
    [Test]
    public async Task StructuralFindingsRemainObservations()
    {
        var production = new CloneScan("production", true, null, null, 4, 2, 30, 20, [], []);
        var tests = new CloneScan("tests", true, null, null, 3, 1, 20, 10, [], []);
        var complexity = new ComplexityQualityReport(true, false, "increased", 20, 3, null, RegressionDetails: []);
        await Assert.That(QualityCommand.Failures(new(production, tests), complexity).Count).IsEqualTo(0);
        var unused = new DeadCodeReport(true, null, [], [new("IDE0051", "src/example.cs", 1, "Unused")]);
        await Assert.That(unused.Failures().Any()).IsFalse();
    }

    [Test]
    public async Task FailedScansCannotBeReportedAsCompleted()
    {
        var scan = new CloneScan("production", false, "scanner unavailable", null, 0, 0, 0, 0, [], []);
        var complexity = new ComplexityQualityReport(false, false, "analysis failed", 0, 0, null);
        var failures = QualityCommand.Failures(new(scan, scan), complexity);
        await Assert.That(failures.Contains("analysis failed")).IsTrue();
        await Assert.That(failures.Contains("scanner unavailable")).IsTrue();
    }

    [Test]
    public async Task TypeScriptParsingErrorsCannotBecomeAnEmptySuccessfulScan()
    {
        var path = Path.Combine(Path.GetTempPath(), "src", "Docs", "Heartbeat.Docs", "lib", "example.ts");
        var json = System.Text.Json.JsonSerializer.Serialize(new[]
        {
            new { filePath = path, messages = new[] { new { ruleId = (string?)null, fatal = true, severity = 2, message = "Unexpected token ;", line = 1, column = 23 } } },
        });
        await Assert.That(() => ComplexityDetector.ParseTypeScript(Path.GetTempPath(), json)).Throws<InvalidDataException>();
    }
}
