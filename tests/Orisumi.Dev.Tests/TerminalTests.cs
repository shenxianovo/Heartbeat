using System.Text.Json;
using Orisumi.Dev;

namespace Orisumi.Dev.Tests;

public sealed class TerminalTests
{
    [Test]
    public async Task RedirectedOutputHasNoEscapeCodesOrTrailingSpaces()
    {
        using var writer = new StringWriter();
        var console = Terminal.Create(writer);
        Terminal.Rows(console, [["a", "[green]long value[/]"], ["longer name", "b"]]);
        Terminal.Fields(console, [("Label", "[red]value[/]")]);

        var text = writer.ToString();
        await Assert.That(text).DoesNotContain("\u001b");
        await Assert.That(text.Split('\n').Any(line => line.TrimEnd('\r').EndsWith(' '))).IsFalse();
        await Assert.That(text).Contains("  longer name   b");
    }

    [Test]
    public async Task RunSummaryReadsTheOutcomeAndFailedSteps()
    {
        var directory = Path.Combine(Path.GetTempPath(), "orisumi-run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var started = DateTimeOffset.UtcNow;
            File.WriteAllText(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(new
            {
                kind = "verify", name = "full-fallback", exitCode = 1,
                startedAt = started, completedAt = started.AddSeconds(95),
            }));
            File.WriteAllText(Path.Combine(directory, "verification.json"), JsonSerializer.Serialize(new
            {
                results = new object[] { new { name = "build", exitCode = 0 }, new { name = "tests", exitCode = 1 } },
            }));
            File.WriteAllText(Path.Combine(directory, "quality.json"), "{}");

            var summary = ArtifactStore.Summarize(new ArtifactRun("run", directory, started));

            await Assert.That(summary.What).IsEqualTo("verify full (fallback) + quality");
            await Assert.That(summary.ExitCode).IsEqualTo(1);
            await Assert.That(summary.Duration).IsEqualTo(TimeSpan.FromSeconds(95));
            await Assert.That(string.Join(",", summary.Failed)).IsEqualTo("tests");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Test]
    public async Task RunsWithoutManifestAreUnfinished()
    {
        var directory = Path.Combine(Path.GetTempPath(), "orisumi-run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var summary = ArtifactStore.Summarize(new ArtifactRun("20261030T050639Z-verify-changed-abc", directory, DateTimeOffset.UtcNow));
            await Assert.That(summary.ExitCode).IsNull();
            await Assert.That(summary.What).IsEqualTo("verify changed");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Test]
    [Arguments("Orisumi.Dev/ArtifactStore`0/Summarize`0(:ArtifactRun)", "ArtifactStore.Summarize")]
    [Arguments("FunctionDeclaration:useDay/FunctionDeclaration:experienceOf", "useDay.experienceOf")]
    [Arguments("FunctionDeclaration:EntityPage", "EntityPage")]
    public async Task AnalyzerSymbolsKeepTheTypeAndFunctionName(string symbol, string expected) =>
        await Assert.That(QualityCommand.ShortSymbol(symbol)).IsEqualTo(expected);
}
