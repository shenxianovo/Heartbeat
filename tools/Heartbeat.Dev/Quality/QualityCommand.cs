using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Parsing;
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
    int CurrentBuildLines,
    int CurrentUnclassifiedLines,
    IReadOnlyList<string> UnclassifiedPaths,
    IReadOnlyList<LanguageDelta> ProductionLanguages);

/// 基点本身也是一条要报告的事实：请求的是什么、解析成哪个提交、能不能量。
internal sealed record QualityBaseReport(
    string Requested,
    string Resolved,
    bool Usable,
    string? Reason,
    IReadOnlyList<string> Suggestions);

internal sealed record QualityReport(
    QualityBaseReport Base,
    string Mode,
    string ArtifactDirectory,
    LineQualityReport? Lines,
    CloneQualityReport? Clones,
    ComplexityQualityReport? Complexity,
    StockBudgetReport? Budget,
    bool Passed,
    IReadOnlyList<string> Failures);

internal sealed class QualityCommand(
    RepositoryContext repository,
    IProcessRunner runner,
    TextWriter output)
{
    private const string GateMode = "gate";
    private const string StockMode = "stock";

    public Command CreateCommand()
    {
        var command = new Command("quality", "Compare structural quality with a Git base");
        var baseRef = new Option<string?>("--base") { Description = $"Git comparison base; '{RewriteLineage.Alias}' resolves to {RewriteLineage.Describe()}" };
        var stock = new Option<bool>("--stock") { Description = "Report accumulated deltas; only the committed stock budget is enforced" };
        var json = new Option<bool>("--json") { Description = "Print the report as JSON" };
        command.Options.Add(baseRef);
        command.Options.Add(stock);
        command.Options.Add(json);
        var loc = new LocCommand(repository, runner, output).CreateCommand();
        command.Subcommands.Add(loc);
        loc.Validators.Add(result =>
        {
            if (result.Parent is CommandResult parent
                && parent.Children.OfType<OptionResult>().Any(option => !option.Implicit
                    && (option.Option == baseRef || option.Option == stock || option.Option == json)))
                result.AddError("Quality comparison options cannot be combined with loc; use 'quality loc --json' for JSON counts.");
        });
        command.SetAction((parse, token) =>
        {
            if (parse.GetValue(baseRef) is { } value)
                return RunAsync(new QualityOptions(value, parse.GetValue(json), parse.GetValue(stock)), token);
            if (parse.Tokens.Count == 1) return Task.FromResult(new HelpAction().Invoke(parse));
            throw new CommandUsageException("quality requires an explicit --base REF.");
        });
        return command;
    }

    public async Task<int> RunAsync(QualityOptions options, CancellationToken cancellationToken)
    {
        var (baseRef, json, stock) = options;
        var mode = stock ? StockMode : GateMode;
        var resolved = RewriteLineage.Resolve(baseRef);
        var limitations = new List<string>
        {
            "LOC is a change signal, not a correctness or maintainability verdict.",
        };
        if (stock)
        {
            limitations.Add(
                "Stock mode reports everything accumulated since the base; it does not gate the current change.");
        }

        return await EvidenceSession.ExecuteAsync(repository, "quality", mode, limitations, async evidence =>
        {
            var run = evidence.Run;
            var commands = evidence.Commands;
            var source = new GitSourceReader(repository, runner);
            var baseline = await source.ReadRevisionAsync(resolved, cancellationToken);
            var usability = BaseUsability.Evaluate(baseRef, baseline);
            if (!usability.Usable)
            {
                var unusable = new QualityReport(
                    new QualityBaseReport(baseRef, baseline.Revision, false, usability.Reason, usability.Suggestions),
                    mode, run.Directory, null, null, null, null, false, [usability.Reason!]);
                await WriteReportAsync(run.Directory, unusable, cancellationToken);
                if (json) await output.WriteLineAsync(JsonSerializer.Serialize(unusable, JsonOptions.Indented));
                else await WriteUnusableBaseAsync(unusable);
                return 1;
            }

            var current = await source.ReadWorktreeAsync(cancellationToken);
            var lines = CompareLines(baseline, current);
            // 三次扫描都用同一个已解析的提交号，不用 `--base` 给的那个名字：`HEAD` 这类可移动的
            // 引用会让缓存的基线工作树在 HEAD 前进后继续被复用，量出来的「增量」就不是相对当前基点的。
            var baseCommit = baseline.Revision;
            var clones = await new CloneDetector(repository, runner)
                .CompareAsync(baseCommit, run.Directory, commands, cancellationToken);
            var complexity = await new ComplexityDetector(repository, runner)
                .CompareAsync(baseCommit, run.Directory, commands, cancellationToken);
            var budget = complexity.Available
                ? StockBudget.Evaluate(repository, complexity.CurrentHotspots)
                : null;
            var failures = Failures(stock, clones, complexity, budget);
            var report = new QualityReport(
                new QualityBaseReport(baseRef, baseline.Revision, true, null, []),
                mode, run.Directory, lines, clones, complexity, budget, failures.Count == 0, failures);
            await WriteReportAsync(run.Directory, report, cancellationToken);
            if (json) await output.WriteLineAsync(JsonSerializer.Serialize(report, JsonOptions.Indented));
            else await WriteHumanAsync(report);
            return report.Passed ? 0 : 1;
        }, notes: output);
    }

    /// <summary>
    /// 谁挂谁不挂集中在这里：clone 与复杂度检测只负责度量，闸门决定在这一层。
    /// gate 模式拦「相对基点新增的」；stock 模式只拦「量不出来」和「存量上限被突破」。
    /// </summary>
    internal static IReadOnlyList<string> Failures(
        bool stock,
        CloneQualityReport clones,
        ComplexityQualityReport complexity,
        StockBudgetReport? budget)
    {
        var failures = new List<string>();
        foreach (var scan in clones.Scans)
        {
            if (!scan.Available)
            {
                failures.Add(scan.Unavailable!);
                continue;
            }
            if (stock || scan.NewClones == 0) continue;
            failures.Add(
                $"{scan.NewClones} new {scan.Scope} clone cluster(s) since the base:{Environment.NewLine}"
                + string.Join(Environment.NewLine, scan.NewFindings.Select(finding => "    " + finding.Describe())));
        }

        if (!complexity.Available)
        {
            failures.Add(complexity.Reason!);
        }
        else if (!stock && complexity.Regressions > 0)
        {
            failures.Add(
                $"{complexity.Regressions} production function(s) crossed complexity 10 or grew above it:{Environment.NewLine}"
                + string.Join(Environment.NewLine, complexity.RegressionDetails!.Select(item => "    " + Describe(item))));
        }

        if (budget is { Passed: false }) failures.Add(budget.Reason!);
        return failures;
    }

    private static string Describe(ComplexityRegression regression) =>
        $"{regression.Current.Path}:{regression.Current.Line} {regression.Current.Symbol} "
        + $"complexity {(regression.BaseComplexity is { } previous ? previous.ToString(System.Globalization.CultureInfo.InvariantCulture) : "absent at base")}"
        + $" -> {regression.Current.Complexity}";

    private static async Task WriteReportAsync(
        string directory,
        QualityReport report,
        CancellationToken cancellationToken) =>
        await File.WriteAllTextAsync(
            Path.Combine(directory, "quality.json"),
            JsonSerializer.Serialize(report, JsonOptions.Indented) + Environment.NewLine,
            cancellationToken);

    private async Task WriteUnusableBaseAsync(QualityReport report)
    {
        await output.WriteLineAsync($"Unusable quality base '{report.Base.Requested}'");
        await output.WriteLineAsync($"  {report.Base.Reason}");
        foreach (var suggestion in report.Base.Suggestions)
        {
            await output.WriteLineAsync($"  - {suggestion}");
        }
        await output.WriteLineAsync("  Nothing was scanned: measuring against this base would only produce zeros.");
        await output.WriteLineAsync($"  evidence: {report.ArtifactDirectory}");
    }

    private async Task WriteHumanAsync(QualityReport report)
    {
        var lines = report.Lines!;
        await output.WriteLineAsync(report.Mode == StockMode
            ? $"Stock measurement against {report.Base.Requested} ({Short(report.Base.Resolved)}) — accumulated, not gated"
            : $"Quality gate against {report.Base.Requested} ({Short(report.Base.Resolved)})");
        await output.WriteLineAsync(
            $"  production LOC: {lines.CurrentProductionLines:N0} ({lines.ProductionDelta:+#;-#;0} vs base {lines.BaseProductionLines:N0})");
        await output.WriteLineAsync($"  test LOC:       {lines.CurrentTestLines:N0}");
        await output.WriteLineAsync($"  tooling LOC:    {lines.CurrentToolingLines:N0}");
        await output.WriteLineAsync($"  build LOC:      {lines.CurrentBuildLines:N0} (project, image and compose files; not production)");
        if (lines.CurrentUnclassifiedLines > 0)
        {
            await output.WriteLineAsync(
                $"  unclassified:   {lines.CurrentUnclassifiedLines:N0} in {lines.UnclassifiedPaths.Count} file(s): "
                + string.Join(", ", lines.UnclassifiedPaths.Take(5)));
        }
        foreach (var language in lines.ProductionLanguages)
        {
            await output.WriteLineAsync($"    {language.Language,-12} {language.Current,7:N0} ({language.Delta:+#;-#;0})");
        }
        foreach (var scan in report.Clones!.Scans)
        {
            await output.WriteLineAsync(scan.Available
                ? $"  {scan.Scope,-10} clones: {scan.Clones} current, {scan.NewClones} new since base, "
                  + $"{scan.DuplicatedLinePercentage:0.00}% duplicated lines"
                : $"  {scan.Scope,-10} clone scan unavailable: {scan.Unavailable}");
            foreach (var finding in scan.NewFindings.Take(5))
            {
                await output.WriteLineAsync($"      new: {finding.Describe()}");
            }
        }
        await WriteComplexityAsync(report);
        await output.WriteLineAsync($"  evidence: {report.ArtifactDirectory}");
        foreach (var failure in report.Failures)
        {
            await output.WriteLineAsync($"  FAILED: {failure}");
        }
    }

    private async Task WriteComplexityAsync(QualityReport report)
    {
        var complexity = report.Complexity!;
        if (!complexity.Available)
        {
            await output.WriteLineAsync($"  complexity scan unavailable: {complexity.Reason}");
            return;
        }

        var budget = report.Budget?.Limit is { } limit
            ? $", stock budget {limit}"
            : string.Empty;
        await output.WriteLineAsync(
            $"  complexity: {complexity.CurrentHotspots} production hotspots above 10{budget}, "
            + $"{complexity.Regressions} new or growing since base");
        foreach (var regression in complexity.RegressionDetails!.Take(5))
        {
            await output.WriteLineAsync($"      {Describe(regression)}");
        }
        await output.WriteLineAsync(
            $"  erosion:    {complexity.CurrentErosion!.Ratio:P2} ({complexity.ErosionDelta:+0.00%;-0.00%;0.00%} vs base; "
            + $"{complexity.CurrentErosion.HighRiskFunctions}/{complexity.CurrentErosion.TotalFunctions} high-risk functions)");
        if (complexity.Coupling is { } coupling)
        {
            await output.WriteLineAsync(
                $"  C# coupling: {coupling.Current.Count} current hotspots, {coupling.Baseline.Count} at base (CA1506, observation only)");
        }
    }

    private static string Short(string revision) => revision.Length > 7 ? revision[..7] : revision;

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

internal sealed record QualityOptions(string Base, bool Json = false, bool Stock = false);
