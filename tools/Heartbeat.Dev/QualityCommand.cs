using System.Text.Json;

namespace Heartbeat.Dev;

internal sealed record LanguageDelta(string Language, int Base, int Current, int Delta);

internal sealed record LineQualityReport(
    string BaseRevision,
    int BaseProductionLines,
    int CurrentProductionLines,
    int ProductionDelta,
    int CurrentTestLines,
    int CurrentToolingLines,
    IReadOnlyList<LanguageDelta> ProductionLanguages);

internal sealed record CloneQualityReport(
    bool Available,
    bool Passed,
    string? Reason,
    string? ReportDirectory,
    int? CurrentClones = null,
    int? NewClones = null,
    int? DuplicatedLines = null,
    double? DuplicatedLinePercentage = null,
    string? ObservationReportDirectory = null);

internal sealed record QualityReport(
    string Base,
    string ArtifactDirectory,
    LineQualityReport Lines,
    CloneQualityReport Clones,
    ComplexityQualityReport Complexity,
    bool Passed);

internal sealed class QualityCommand(
    RepositoryContext repository,
    IProcessRunner runner,
    TextWriter output)
{
    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            await output.WriteLineAsync("Usage: heartbeat-dev quality --base REF [--json]");
            return 0;
        }
        var (baseRef, json) = Parse(args);
        return await EvidenceSession.ExecuteAsync(repository, "quality", "git-delta",
            ["LOC is a change signal, not a correctness or maintainability verdict."], async evidence =>
            {
                var run = evidence.Run;
                var commands = evidence.Commands;
                var source = new GitSourceReader(repository, runner);
                var baseline = await source.ReadRevisionAsync(baseRef, cancellationToken);
                var current = await source.ReadWorktreeAsync(cancellationToken);
                var lines = CompareLines(baseline, current);

                var clones = await new CloneDetector(repository, runner)
                    .CompareAsync(baseRef, run.Directory, commands, cancellationToken);
                var complexity = await new ComplexityDetector(repository, runner)
                    .CompareAsync(baseRef, run.Directory, commands, cancellationToken);
                var passed = clones.Passed && complexity.Passed;
                var report = new QualityReport(baseRef, run.Directory, lines, clones, complexity, passed);
                var reportPath = Path.Combine(run.Directory, "quality.json");
                await File.WriteAllTextAsync(
                    reportPath,
                    JsonSerializer.Serialize(report, JsonOptions.Indented) + Environment.NewLine,
                    cancellationToken);
                if (json) await output.WriteLineAsync(JsonSerializer.Serialize(report, JsonOptions.Indented));
                else await WriteHumanAsync(report);
                return passed ? 0 : 1;
            });
    }

    private async Task WriteHumanAsync(QualityReport report)
    {
        await output.WriteLineAsync($"Quality comparison against {report.Base}");
        await output.WriteLineAsync(
            $"  production LOC: {report.Lines.CurrentProductionLines:N0} ({report.Lines.ProductionDelta:+#;-#;0})");
        await output.WriteLineAsync($"  test LOC:       {report.Lines.CurrentTestLines:N0}");
        await output.WriteLineAsync($"  tooling LOC:    {report.Lines.CurrentToolingLines:N0}");
        foreach (var language in report.Lines.ProductionLanguages)
        {
            await output.WriteLineAsync($"    {language.Language,-12} {language.Current,7:N0} ({language.Delta:+#;-#;0})");
        }
        await output.WriteLineAsync(report.Clones.Available
            ? $"  production clones: {report.Clones.CurrentClones} current, {report.Clones.NewClones} new, {report.Clones.DuplicatedLinePercentage:0.00}% duplicated lines"
            : $"  clone scan unavailable: {report.Clones.Reason}");
        await output.WriteLineAsync(report.Complexity.Available
            ? $"  complexity: {report.Complexity.CurrentHotspots} current hotspots, {report.Complexity.Regressions} regressions ({(report.Complexity.Passed ? "passed" : "failed")}){Environment.NewLine}" +
              $"  erosion:    {report.Complexity.CurrentErosion!.Ratio:P2} ({report.Complexity.ErosionDelta:+0.00%;-0.00%;0.00%} vs base; " +
              $"{report.Complexity.CurrentErosion.HighRiskFunctions}/{report.Complexity.CurrentErosion.TotalFunctions} high-risk functions)"
            : $"  complexity scan unavailable: {report.Complexity.Reason}");
        if (report.Complexity.Coupling is { } coupling)
            await output.WriteLineAsync($"  C# coupling: {coupling.Current.Count} current hotspots, {coupling.Baseline.Count} at base (CA1506, observation only)");
        await output.WriteLineAsync($"  evidence: {report.ArtifactDirectory}");
    }

    private static (string Base, bool Json) Parse(string[] args)
    {
        string? baseRef = null;
        var json = false;
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--base":
                    if (++index >= args.Length) throw new CommandUsageException("Missing value for --base.");
                    baseRef = args[index];
                    break;
                case "--json":
                    json = true;
                    break;
                default:
                    throw new CommandUsageException($"Unknown quality option '{args[index]}'.");
            }
        }
        return (baseRef ?? throw new CommandUsageException("quality requires an explicit --base REF."), json);
    }

    internal static LineQualityReport CompareLines(SourceSnapshot baseline, SourceSnapshot current)
    {
        var languages = baseline.ProductionByLanguage.Keys
            .Union(current.ProductionByLanguage.Keys, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select(language =>
            {
                var before = baseline.ProductionByLanguage.GetValueOrDefault(language);
                var after = current.ProductionByLanguage.GetValueOrDefault(language);
                return new LanguageDelta(language, before, after, after - before);
            })
            .ToArray();
        return new LineQualityReport(
            baseline.Revision,
            baseline.ProductionLines,
            current.ProductionLines,
            current.ProductionLines - baseline.ProductionLines,
            current.TestLines,
            current.ToolingLines,
            languages);
    }
}
