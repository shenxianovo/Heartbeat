using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Spectre.Console;

namespace Orisumi.Dev;

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
        var baseRef = new Option<string>("--base") { Required = true, Description = "Git comparison base" };
        var json = new Option<bool>("--json") { Description = "Print the report as JSON" };
        command.Options.Add(baseRef);
        command.Options.Add(json);
        command.SetAction((parse, token) => RunAsync(new QualityOptions(parse.GetValue(baseRef)!, parse.GetValue(json)), token));
        return command;
    }

    public Task<int> RunAsync(QualityOptions options, CancellationToken token) =>
        EvidenceSession.ExecuteAsync(repository, "quality", "observations",
            ["Structural findings guide review; completing the scans is not a maintainability verdict."],
            evidence => RunChecksAsync(options, evidence, token), notes: options.Json ? null : output);

    internal async Task<int> RunChecksAsync(QualityOptions options, EvidenceSession evidence, CancellationToken token)
    {
        if (options.Json)
        {
            var analyzed = await AnalyzeAsync(options, evidence, token);
            await output.WriteLineAsync(JsonSerializer.Serialize(analyzed, JsonOptions.Indented));
            return analyzed.Completed ? 0 : 1;
        }
        var console = Terminal.Create(output);
        var report = await Terminal.SpinAsync(console, $"Analyzing quality against {Terminal.Esc(options.Base)}",
            () => AnalyzeAsync(options, evidence, token));
        Write(console, repository, report);
        return report.Completed ? 0 : 1;
    }

    /// 运行全部扫描，把报告写进证据目录。
    internal async Task<QualityReport> AnalyzeAsync(QualityOptions options, EvidenceSession evidence, CancellationToken token)
    {
        var source = new GitSourceReader(repository, runner);
        var baseline = await source.ReadRevisionAsync(options.Base, token);
        var usability = BaseUsability.Evaluate(options.Base, baseline);
        var baseReport = new QualityBaseReport(options.Base, baseline.Revision,
            usability.Usable, usability.Reason, usability.Suggestions);
        if (!usability.Usable)
            return await SaveAsync(new(baseReport, evidence.Run.Directory, null, null, null, null,
                false, [usability.Reason!]), token);

        var current = await source.ReadWorktreeAsync(token);
        var clones = await new CloneDetector(repository, runner)
            .CompareAsync(baseline.Revision, baseline, current, evidence.Run.Directory, evidence.Commands, token);
        var complexity = await new ComplexityDetector(repository, runner)
            .CompareAsync(baseline.Revision, evidence.Run.Directory, evidence.Commands, token);
        var deadCode = await new DeadCodeDetector(repository, runner).CompareAsync(
            baseline.Revision, evidence.Run.Directory, evidence.Commands, complexity.BaselineCSharpComplete, complexity.CurrentCSharpComplete, token);
        var failures = Failures(clones, complexity).Concat(deadCode.Failures()).ToArray();
        return await SaveAsync(new(baseReport, evidence.Run.Directory, CompareLines(baseline, current),
            clones, complexity, deadCode, failures.Length == 0, failures), token);
    }

    internal static IReadOnlyList<string> Failures(CloneQualityReport clones, ComplexityQualityReport complexity)
    {
        var failures = clones.Scans.Where(scan => !scan.Available)
            .Select(scan => scan.Unavailable ?? $"The {scan.Scope} clone scan did not complete.").ToList();
        if (!complexity.Available) failures.Add(complexity.Reason ?? "Complexity analysis did not complete.");
        return failures;
    }

    private static async Task<QualityReport> SaveAsync(QualityReport report, CancellationToken token)
    {
        await File.WriteAllTextAsync(Path.Combine(report.ArtifactDirectory, "quality.json"),
            JsonSerializer.Serialize(report, JsonOptions.Indented), token);
        return report;
    }

    /// 相对基点新增的发现数；复杂度在基点无法分析时不计。
    internal static int NewFindings(QualityReport report) =>
        (report.Clones?.NewClones ?? 0)
        + (report.Complexity is { BaselineComplete: true } complexity ? complexity.Regressions : 0)
        + (report.DeadCode?.NewFindings.Count ?? 0);

    internal static string Verdict(QualityReport report) => !report.Completed
        ? Terminal.Verdict(Outcome.Failed, "incomplete")
        : NewFindings(report) is var found and > 0
            ? Terminal.Verdict(Outcome.Attention, $"{found} new {(found == 1 ? "finding" : "findings")}")
            : Terminal.Verdict(Outcome.Passed, "nothing new");

    internal static void Write(IAnsiConsole console, RepositoryContext repository, QualityReport report)
    {
        var resolved = report.Base.Resolved.Length >= 7 ? report.Base.Resolved[..7] : report.Base.Resolved;
        Terminal.Heading(console, "Quality", Terminal.Esc($"against {report.Base.Requested} ({resolved})"), Verdict(report));
        if (report.Lines is { } lines)
        {
            static string New(int count) => count > 0 ? $"[yellow]{Terminal.Number(count)}[/]" : "0";
            List<string[]> rows =
            [
                ["", "[grey]current[/]", "[grey]new[/]"],
                ["Production LOC", Terminal.Number(lines.CurrentProductionLines), Terminal.Delta(lines.ProductionDelta)],
                ["Clones", Terminal.Number(report.Clones!.Scans.Sum(scan => scan.Clones)), New(report.Clones.NewClones)],
            ];
            if (report.Complexity is { CurrentComplete: true } complexity)
            {
                rows.Add(["Complex functions > 10", Terminal.Number(complexity.CurrentHotspots),
                    complexity.BaselineComplete ? New(complexity.Regressions) : "[grey]n/a[/]"]);
                if (complexity.CurrentErosion is { } erosion)
                    rows.Add(["Complexity burden", erosion.Ratio.ToString("P1", CultureInfo.InvariantCulture),
                        complexity.ErosionDelta is { } delta
                            ? (delta * 100).ToString("+0.0;-0.0;±0.0", CultureInfo.InvariantCulture) + "pp" : "[grey]n/a[/]"]);
            }
            rows.Add(["Unused code", Terminal.Number(report.DeadCode!.Current.Count), New(report.DeadCode.NewFindings.Count)]);
            Terminal.Rows(console, rows, 1, 2);
        }
        WriteFindings(console, report);
        foreach (var suggestion in report.Base.Suggestions) Terminal.Hint(console, suggestion);
        console.WriteLine();
        console.MarkupLine($"  [grey]evidence[/]  {Terminal.Esc(Terminal.Path(repository, Path.Combine(report.ArtifactDirectory, "quality.json")))}");
    }

    /// 只展开新增的发现和未完成的扫描；现有的发现见 quality.json。
    internal static void WriteFindings(IAnsiConsole console, QualityReport report)
    {
        var findings = NewFindingLines(report).ToArray();
        if (findings.Length > 0) console.WriteLine();
        foreach (var (kind, text) in findings) console.MarkupLine($"    [yellow]{kind,-8}[/] {Terminal.Esc(text)}");
        if (report.Failures.Count > 0) console.WriteLine();
        foreach (var failure in report.Failures) console.MarkupLine($"    {Terminal.Mark(Outcome.Failed)} {Terminal.Esc(failure)}");
    }

    private static IEnumerable<(string Kind, string Text)> NewFindingLines(QualityReport report) =>
        NewClones(report).Concat(NewComplexity(report)).Concat(NewUnused(report));

    private static IEnumerable<(string, string)> NewClones(QualityReport report) =>
        (report.Clones?.Scans.SelectMany(scan => scan.NewFindings) ?? []).Select(finding => ("clone",
            $"{finding.FirstFile}:{finding.FirstStart}-{finding.FirstEnd} ↔ "
            + $"{finding.SecondFile}:{finding.SecondStart}-{finding.SecondEnd} · {finding.Lines} lines"));

    private static IEnumerable<(string, string)> NewComplexity(QualityReport report) =>
        (report.Complexity is { BaselineComplete: true } complexity ? complexity.RegressionDetails ?? [] : [])
            .Select(regression => ("complex",
                $"{regression.Current.Path}:{regression.Current.Line} {ShortSymbol(regression.Current.Symbol)} · "
                + $"{regression.Current.Complexity}" + (regression.BaseComplexity is { } before ? $" (was {before})" : " (new)")));

    private static IEnumerable<(string, string)> NewUnused(QualityReport report) =>
        (report.DeadCode?.NewFindings ?? []).Select(finding => ("unused", $"{finding.Path}:{finding.Line} {finding.Symbol} · {finding.Rule}"));

    /// 分析器的符号名带命名空间、泛型元数和参数，只保留最后的类型和函数名。
    internal static string ShortSymbol(string symbol)
    {
        var withoutParameters = Regex.Replace(symbol, @"\(.*\)", "");
        var segments = withoutParameters.Split('/')
            .Select(segment => Regex.Replace(Regex.Replace(segment, @"^[A-Za-z]+:", ""), @"`\d+", ""))
            .Where(segment => segment.Length > 0)
            .ToArray();
        return string.Join('.', segments.TakeLast(2));
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
