using System.CommandLine;
using System.CommandLine.Help;
using System.Diagnostics;
using System.Text.Json;
using Spectre.Console;

namespace Orisumi.Dev;

internal sealed record CombinedResult(int Verification, int? Quality)
{
    public int ExitCode => Quality == 130 ? 130 : Verification != 0 ? Verification : Quality ?? 1;
}

internal sealed class VerificationCommand(
    RepositoryContext repository,
    IProcessRunner runner,
    TextWriter output,
    TextWriter? error = null)
{
    private const string Limitation = "Container startup and browser interaction are verified separately.";
    private const int FailureLines = 20;
    private const string QualityLimitation = "Quality findings guide review; scans must complete, but findings do not fail the run.";

    public Command CreateCommand()
    {
        var command = new Command("verify", "Verify code and tests");
        command.SetAction(parse => new HelpAction().Invoke(parse));

        var checks = new Command("checks", "Run build and automated checks; scope selects changed paths or all checks");
        var scope = new Option<string>("--scope")
        {
            Description = "Check selection; all excludes scenarios and quality analysis",
            DefaultValueFactory = _ => "changed",
        };
        scope.AcceptOnlyFromAmong("changed", "all");
        var baseRef = new Option<string?>("--base") { Description = "Git comparison base for changed scope" };
        var quality = new Option<bool>("--quality") { Description = "Also analyze quality against the same base (changed scope only)" };
        var plan = PlanOption();
        var json = JsonOption();
        checks.Options.Add(scope);
        checks.Options.Add(baseRef);
        checks.Options.Add(quality);
        checks.Options.Add(plan);
        checks.Options.Add(json);
        checks.SetAction((parse, token) => RunAsync(new VerificationRequest(
            parse.GetValue(scope) == "all" ? "full" : "changed", parse.GetValue(baseRef),
            parse.GetValue(plan), parse.GetValue(json), parse.GetValue(quality)), token));
        command.Subcommands.Add(checks);
        command.Subcommands.Add(new ScenarioCommand(repository, runner, output).CreateCommand());
        return command;
    }

    private static Option<bool> PlanOption() => new("--plan") { Description = "Print the plan without executing it" };

    private static Option<bool> JsonOption() => new("--json") { Description = "Print the plan or execution result as JSON" };

    public async Task<int> RunAsync(VerificationRequest request, CancellationToken cancellationToken)
    {
        request = await request.ResolveAsync(runner, cancellationToken);
        var plan = await VerificationPlanner.CreateAsync(repository, runner, request, cancellationToken);
        if (request.PlanOnly)
        {
            await VerificationReporter.WriteAsync(repository, output, plan, request.Json, request.Quality);
            return 0;
        }
        var progress = request.Json ? error ?? Console.Error : output;
        var console = Terminal.Create(progress);
        VerificationReporter.WriteHeading(console, plan, request.Quality);
        var execution = new VerificationCommand(repository, runner, progress);
        string[] limitations = request.Quality ? [Limitation, QualityLimitation] : [Limitation];
        string? directory = null;
        var exitCode = 1;
        var clock = Stopwatch.StartNew();
        try
        {
            exitCode = await EvidenceSession.ExecuteAsync(repository, "verify", plan.Mode, limitations, evidence =>
            {
                directory = evidence.Run.Directory;
                return request.Quality
                    ? execution.RunWithQualityAsync(plan, request.Base!, evidence, cancellationToken)
                    : execution.RunPlanAsync(plan, evidence, cancellationToken);
            }, notes: progress);
            return exitCode;
        }
        catch (OperationCanceledException)
        {
            exitCode = 130;
            throw;
        }
        finally
        {
            if (directory is not null) VerificationReporter.WriteVerdict(console, repository, exitCode, clock.Elapsed, directory);
            if (request.Json && directory is not null)
            {
                await output.WriteLineAsync(JsonSerializer.Serialize(new
                {
                    exitCode,
                    evidence = directory,
                    verification = await ReadReportAsync(directory, "verification.json"),
                    quality = await ReadReportAsync(directory, "quality.json"),
                }, JsonOptions.Indented));
            }
        }
    }

    private static async Task<JsonElement?> ReadReportAsync(string directory, string name)
    {
        var path = Path.Combine(directory, name);
        return File.Exists(path) ? JsonSerializer.Deserialize<JsonElement>(await File.ReadAllTextAsync(path)) : null;
    }

    private async Task<int> RunWithQualityAsync(
        VerificationPlan plan, string commit, EvidenceSession evidence, CancellationToken cancellationToken)
    {
        var result = await CombineAsync(
            () => RunPlanAsync(plan, evidence, cancellationToken),
            async () =>
            {
                var console = Terminal.Create(output);
                var clock = Stopwatch.StartNew();
                var report = await Terminal.SpinAsync(console, "Analyzing quality",
                    () => new QualityCommand(repository, runner, output).AnalyzeAsync(new QualityOptions(commit), evidence, cancellationToken));
                VerificationReporter.WriteStep(console, "quality", VerificationReporter.NameWidth(plan),
                    report.Completed ? Outcome.Passed : Outcome.Failed, clock.Elapsed, QualityCommand.Verdict(report));
                QualityCommand.WriteFindings(console, report);
                return report.Completed ? 0 : 1;
            },
            cancellationToken);
        return result.ExitCode;
    }

    /// 质量扫描在验证之后运行；任一步取消都按取消报告，不能被另一步的结果覆盖。
    internal static async Task<CombinedResult> CombineAsync(
        Func<Task<int>> verify, Func<Task<int>> quality, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int verification;
        try { verification = await verify(); }
        catch (OperationCanceledException) { return new CombinedResult(130, null); }
        if (verification == 130) return new CombinedResult(verification, null);
        if (cancellationToken.IsCancellationRequested) return new CombinedResult(verification, 130);
        try { return new CombinedResult(verification, await quality()); }
        catch (OperationCanceledException) { return new CombinedResult(verification, 130); }
    }

    internal async Task<int> RunPlanAsync(VerificationPlan plan, EvidenceSession evidence, CancellationToken cancellationToken)
    {
        var console = Terminal.Create(output);
        var width = VerificationReporter.NameWidth(plan);
        var run = evidence.Run;
        var commands = evidence.Commands;
        var exitCode = 0;
        var results = new List<(string Name, int ExitCode, string Log)>();
        try
        {
            foreach (var step in plan.Steps)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var arguments = step.Arguments;
                var command = $"{step.FileName} {string.Join(' ', arguments.Select(Quote))}";
                commands.Add(command);
                var log = Path.Combine(run.Directory, $"{step.Name}.log");
                var clock = Stopwatch.StartNew();
                ProcessResult result;
                try
                {
                    result = await Terminal.SpinAsync(console, $"Running {Terminal.Esc(step.Name)}",
                        () => runner.CaptureAsync(step.FileName, arguments, null, cancellationToken));
                }
                catch (CapturedProcessCancelledException cancelled)
                {
                    await File.WriteAllTextAsync(log, cancelled.StdOut + cancelled.StdErr, CancellationToken.None);
                    results.Add((step.Name, 130, log));
                    VerificationReporter.WriteStep(console, step.Name, width, Outcome.Cancelled, clock.Elapsed);
                    throw;
                }
                await File.WriteAllTextAsync(log, result.StdOut + result.StdErr, cancellationToken);
                results.Add((step.Name, result.ExitCode, log));
                VerificationReporter.WriteStep(console, step.Name, width, Terminal.FromExitCode(result.ExitCode), clock.Elapsed,
                    result.ExitCode is 0 or 130 ? "" : $"[grey]exit {result.ExitCode}[/]");
                if (result.ExitCode == 130) { exitCode = 130; return 130; }
                if (result.ExitCode == 0) continue;
                if (exitCode == 0) exitCode = result.ExitCode;
                WriteFailure(console, result.StdErr.Length > 0 ? result.StdErr : result.StdOut, log);
            }
            return exitCode;
        }
        catch (OperationCanceledException)
        {
            exitCode = 130;
            throw;
        }
        catch (Exception)
        {
            exitCode = 1;
            throw;
        }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(run.Directory, "verification.json"),
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    plan.Mode, plan.Base, plan.ChangedPaths, exitCode,
                    results = results.Select(result => new { result.Name, result.ExitCode, result.Log }),
                }, JsonOptions.Indented), CancellationToken.None);
        }
    }

    /// 失败步骤输出的最后几行；完整输出在日志里。
    private void WriteFailure(IAnsiConsole console, string text, string log)
    {
        foreach (var line in text.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.Trim().Length > 0).TakeLast(FailureLines))
            console.MarkupLine($"      [grey]{Terminal.Esc(line)}[/]");
        console.MarkupLine($"      [grey]log[/] {Terminal.Esc(Terminal.Path(repository, log))}");
    }

    private static string Quote(string value) => value.Contains(' ', StringComparison.Ordinal) ? $"\"{value}\"" : value;
}
