using Orisumi.Dev;

namespace Orisumi.Dev.Tests;

public sealed class QualityObservationTests
{
    [Test]
    public async Task BaselineAnalysisReadsTheSolutionNameFromThatRevision()
    {
        var root = Path.Combine(Path.GetTempPath(), "orisumi-analysis-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "PreviousName.slnx"),
                "<Solution><Project Path=\"src/Example/Example.csproj\" /></Solution>");
            var output = Path.Combine(root, "analysis.xml");
            CSharpAnalysisBuild.Prepare(root, output);
            var project = System.Xml.Linq.XDocument.Load(output).Descendants("Project").Single();
            await Assert.That(project.Attribute("Path")!.Value)
                .IsEqualTo(Path.Combine(root, "src", "Example", "Example.csproj"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Test]
    public async Task FailedScansCannotBeReportedAsCompleted()
    {
        var scan = new CloneScan("production", false, "scanner unavailable", null, 0, 0, 0, 0, [], []);
        var complexity = new ComplexityQualityReport(false, false, "analysis failed", 0, 0, null);
        var failures = QualityCommand.Failures(new(scan, scan, scan), complexity);
        await Assert.That(failures.Contains("analysis failed")).IsTrue();
        await Assert.That(failures.Contains("scanner unavailable")).IsTrue();
    }

    [Test]
    public async Task TypeScriptParsingErrorsCannotBecomeAnEmptySuccessfulScan()
    {
        var path = Path.Combine(Path.GetTempPath(), "src", "Docs", "Orisumi.Docs", "lib", "example.ts");
        var json = System.Text.Json.JsonSerializer.Serialize(new[]
        {
            new { filePath = path, messages = new[] { new { ruleId = (string?)null, fatal = true, severity = 2, message = "Unexpected token ;", line = 1, column = 23 } } },
        });
        await Assert.That(() => ComplexityDetector.ParseTypeScript(Path.GetTempPath(), json)).Throws<InvalidDataException>();
    }
}
