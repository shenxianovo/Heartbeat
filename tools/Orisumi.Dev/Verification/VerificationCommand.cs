using System.CommandLine;
using System.CommandLine.Help;
using System.Text.Json;

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
            await VerificationReporter.WriteAsync(output, plan, request.Json, request.Quality);
            return 0;
        }
        var progress = request.Json ? error ?? Console.Error : output;
        await VerificationReporter.WriteAsync(progress, plan, false, request.Quality);
        var execution = new VerificationCommand(repository, runner, progress);
        string[] limitations = request.Quality ? [Limitation, QualityLimitation] : [Limitation];
        string? directory = null;
        var exitCode = 1;
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
            () => new QualityCommand(repository, runner, output).RunChecksAsync(new QualityOptions(commit), evidence, cancellationToken),
            cancellationToken);
        await output.WriteLineAsync($"Summary: verification exit {result.Verification}; quality "
            + (result.Quality is { } code ? $"exit {code}" : "not run") + $"; evidence: {evidence.Run.Directory}");
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
        var run = evidence.Run;
        var commands = evidence.Commands;
        var exitCode = 0;
        var results = new List<(string Name, int ExitCode, string Log)>();
        await output.WriteLineAsync($"Verification evidence: {run.Directory}");
        try
        {
            foreach (var step in plan.Steps)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var arguments = step.Arguments;
                var command = $"{step.FileName} {string.Join(' ', arguments.Select(Quote))}";
                commands.Add(command);
                await output.WriteLineAsync($"Running {step.Name} ...");
                var log = Path.Combine(run.Directory, $"{step.Name}.log");
                ProcessResult result;
                try
                {
                    result = await runner.CaptureAsync(step.FileName, arguments, null, cancellationToken);
                }
                catch (CapturedProcessCancelledException cancelled)
                {
                    await File.WriteAllTextAsync(log, cancelled.StdOut + cancelled.StdErr, CancellationToken.None);
                    results.Add((step.Name, 130, log));
                    throw;
                }
                await File.WriteAllTextAsync(log, result.StdOut + result.StdErr, cancellationToken);
                results.Add((step.Name, result.ExitCode, log));
                if (result.ExitCode == 130) { exitCode = 130; return 130; }
                if (result.ExitCode == 0) continue;
                if (exitCode == 0) exitCode = result.ExitCode;
                await output.WriteAsync(result.StdErr.Length > 0 ? result.StdErr : result.StdOut);
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
            await WriteSummaryAsync(results);
        }
    }

    private async Task WriteSummaryAsync(IEnumerable<(string Name, int ExitCode, string Log)> results)
    {
        await output.WriteLineAsync("Verification summary:");
        foreach (var result in results)
        {
            var status = result.ExitCode switch { 0 => "succeeded", 130 => "cancelled", _ => "failed" };
            await output.WriteLineAsync($"  {result.Name}: {status} (exit {result.ExitCode}); log: {result.Log}");
        }
    }

    private static string Quote(string value) => value.Contains(' ', StringComparison.Ordinal) ? $"\"{value}\"" : value;
}
