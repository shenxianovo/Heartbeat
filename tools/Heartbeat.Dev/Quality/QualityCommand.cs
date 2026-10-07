using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Parsing;
using System.Text.Json;

namespace Heartbeat.Dev;

internal sealed record LanguageDelta(string Language, int Base, int Current, int Delta);
internal sealed record LineQualityReport(
    string BaseRevision, int BaseProductionLines, int CurrentProductionLines, int ProductionDelta,
    int CurrentTestLines, int CurrentToolingLines, int CurrentBuildLines, int CurrentUnclassifiedLines,
    IReadOnlyList<string> UnclassifiedPaths, IReadOnlyList<LanguageDelta> ProductionLanguages);
internal sealed record QualityBaseReport(
    string Requested, string Resolved, bool Usable, string? Reason, IReadOnlyList<string> Suggestions);
internal sealed record QualityReport(
    QualityBaseReport Base, string ArtifactDirectory, LineQualityReport? Lines,
    CloneQualityReport? Clones, ComplexityQualityReport? Complexity, DeadCodeReport? DeadCode,
    bool Completed, IReadOnlyList<string> Failures);

internal sealed class QualityCommand(RepositoryContext repository, IProcessRunner runner, TextWriter output)
{
    public Command CreateCommand()
    {
        var command = new Command("quality", "Observe structural quality relative to an explicit Git base");
        var baseRef = new Option<string?>("--base") { Description = "Git comparison base" };
        var json = new Option<bool>("--json") { Description = "Print the report as JSON" };
        command.Options.Add(baseRef);
        command.Options.Add(json);
        var loc = new LocCommand(repository, runner, output).CreateCommand();
        command.Subcommands.Add(loc);
        loc.Validators.Add(result =>
        {
            if (result.Parent is CommandResult parent
                && parent.Children.OfType<OptionResult>().Any(option => !option.Implicit && option.Option == baseRef))
                result.AddError("Use quality loc for current counts; --base belongs to quality comparison.");
        });
        command.SetAction((parse, token) =>
        {
            if (parse.GetValue(baseRef) is { } value) return RunAsync(new QualityOptions(value, parse.GetValue(json)), token);
            if (parse.Tokens.Count == 1) return Task.FromResult(new HelpAction().Invoke(parse));
            throw new CommandUsageException("quality requires an explicit --base REF.");
        });
        return command;
    }

    public Task<int> RunAsync(QualityOptions options, CancellationToken token) =>
        EvidenceSession.ExecuteAsync(repository, "quality", "observations",
            ["Structural findings guide review; completing the scans is not a maintainability verdict."],
            evidence => RunChecksAsync(options, evidence, token), notes: options.Json ? null : output);

    internal async Task<int> RunChecksAsync(QualityOptions options, EvidenceSession evidence, CancellationToken token)
    {
        var source = new GitSourceReader(repository, runner);
        var baseline = await source.ReadRevisionAsync(options.Base, token);
        var usability = BaseUsability.Evaluate(options.Base, baseline);
        var baseReport = new QualityBaseReport(options.Base, baseline.Revision,
            usability.Usable, usability.Reason, usability.Suggestions);
        if (!usability.Usable)
            return await ReportAsync(new(baseReport, evidence.Run.Directory, null, null, null, null,
                false, [usability.Reason!]), options.Json, token);

        var current = await source.ReadWorktreeAsync(token);
        var clones = await new CloneDetector(repository, runner)
            .CompareAsync(baseline.Revision, baseline, current, evidence.Run.Directory, evidence.Commands, token);
        var complexity = await new ComplexityDetector(repository, runner)
            .CompareAsync(baseline.Revision, evidence.Run.Directory, evidence.Commands, token);
        var deadCode = await new DeadCodeDetector(repository, runner).CompareAsync(
            baseline.Revision, evidence.Run.Directory, evidence.Commands, complexity.BaselineCSharpComplete, complexity.CurrentCSharpComplete, token);
        var failures = Failures(clones, complexity).Concat(deadCode.Failures()).ToArray();
        return await ReportAsync(new(baseReport, evidence.Run.Directory, CompareLines(baseline, current),
            clones, complexity, deadCode, failures.Length == 0, failures), options.Json, token);
    }

    internal static IReadOnlyList<string> Failures(CloneQualityReport clones, ComplexityQualityReport complexity)
    {
        var failures = clones.Scans.Where(scan => !scan.Available)
            .Select(scan => scan.Unavailable ?? $"The {scan.Scope} clone scan did not complete.").ToList();
        if (!complexity.Available) failures.Add(complexity.Reason ?? "Complexity analysis did not complete.");
        return failures;
    }

    private async Task<int> ReportAsync(QualityReport report, bool json, CancellationToken token)
    {
        await File.WriteAllTextAsync(Path.Combine(report.ArtifactDirectory, "quality.json"),
            JsonSerializer.Serialize(report, JsonOptions.Indented), token);
        if (json) await output.WriteLineAsync(JsonSerializer.Serialize(report, JsonOptions.Indented));
        else
        {
            await output.WriteLineAsync($"Quality observations against {report.Base.Requested} ({report.Base.Resolved[..7]})");
            if (report.Lines is { } lines)
            {
                await output.WriteLineAsync($"  production LOC: {lines.CurrentProductionLines:N0} ({lines.ProductionDelta:+#;-#;0} vs base)");
                foreach (var scan in report.Clones!.Scans)
                {
                    await output.WriteLineAsync($"  {scan.Scope} clones: {scan.Clones} current; {scan.NewClones} new");
                    foreach (var finding in scan.NewFindings) await output.WriteLineAsync("    " + finding.Describe());
                }
                if (report.Complexity is { CurrentComplete: true } complexity)
                {
                    await output.WriteLineAsync($"  complex functions above 10: {complexity.CurrentHotspots}; comparison "
                        + (complexity.BaselineComplete ? $"new or increased: {complexity.Regressions}" : "unavailable for this base"));
                    foreach (var hotspot in complexity.Hotspots ?? [])
                        await output.WriteLineAsync($"    {hotspot.Path}:{hotspot.Line} {hotspot.Symbol}: {hotspot.Complexity}");
                    if (complexity.CurrentErosion is { } erosion)
                        await output.WriteLineAsync($"  complexity burden ratio: {erosion.Ratio:P2}");
                    foreach (var module in complexity.Modules ?? [])
                        await output.WriteLineAsync($"    {module.Module}: {module.Current.TotalFunctions} functions, {module.Current.HighRiskFunctions} above 10");
                }
                await output.WriteLineAsync($"  unused code/dependencies: {report.DeadCode!.Current.Count} current; {report.DeadCode.NewFindings.Count} new");
                foreach (var finding in report.DeadCode.NewFindings) await output.WriteLineAsync("    " + finding.Describe());
            }
            foreach (var failure in report.Failures) await output.WriteLineAsync("  INCOMPLETE: " + failure);
            await output.WriteLineAsync($"  evidence: {report.ArtifactDirectory}");
        }
        return report.Completed ? 0 : 1;
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
            current.BuildLines,
            current.UnclassifiedLines,
            current.UnclassifiedPaths,
            languages);
    }
}

internal sealed record QualityOptions(string Base, bool Json = false);
