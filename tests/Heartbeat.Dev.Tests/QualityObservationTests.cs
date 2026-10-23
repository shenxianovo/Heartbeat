using Heartbeat.Dev;

namespace Heartbeat.Dev.Tests;

public sealed class QualityObservationTests
{
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
