using System.CommandLine;
using System.CommandLine.Help;

namespace Orisumi.Dev;

internal sealed record CombinedResult(int Verification, int? Quality)
{
    public int ExitCode => Quality == 130 ? 130 : Verification != 0 ? Verification : Quality ?? 1;
}

internal sealed class VerificationCommand(
    RepositoryContext repository,
    IProcessRunner runner,
    TextWriter output)
{
    private const string Limitation = "Container startup and browser interaction are verified separately.";
    private const string QualityLimitation = "Quality findings guide review; scans must complete, but findings do not fail the run.";

    public Command CreateCommand()
    {
        var command = new Command("verify", "Verify code and tests");
        command.SetAction(parse => new HelpAction().Invoke(parse));

        var changed = new Command("changed", "Check changed paths; a clean worktree requires --base");
        var baseRef = new Option<string?>("--base") { Description = "Git comparison base" };
        var quality = new Option<bool>("--quality") { Description = "Also collect structural quality observations against the same base" };
        var changedPlan = PlanOption();
        var changedJson = JsonOption();
        changed.Options.Add(baseRef);
        changed.Options.Add(quality);
        changed.Options.Add(changedPlan);
        changed.Options.Add(changedJson);
        changed.SetAction((parse, token) => RunAsync(new VerificationRequest("changed", parse.GetValue(baseRef),
            parse.GetValue(changedPlan), parse.GetValue(changedJson), parse.GetValue(quality)), token));

        var full = new Command("full", "Run all code and test checks");
        var fullPlan = PlanOption();
        var fullJson = JsonOption();
        full.Options.Add(fullPlan);
        full.Options.Add(fullJson);
        full.SetAction((parse, token) => RunAsync(new VerificationRequest("full", null,
            parse.GetValue(fullPlan), parse.GetValue(fullJson)), token));

        command.Subcommands.Add(changed);
        command.Subcommands.Add(full);
        command.Subcommands.Add(new ScenarioCommand(repository, runner, output).CreateCommand());
        return command;
    }

    private static Option<bool> PlanOption() => new("--plan") { Description = "Print the plan without executing it" };

    private static Option<bool> JsonOption() => new("--json") { Description = "Print the plan as JSON" };

    public async Task<int> RunAsync(VerificationRequest request, CancellationToken cancellationToken)
    {
        request = await request.ResolveAsync(runner, cancellationToken);
        var plan = await VerificationPlanner.CreateAsync(repository, runner, request, cancellationToken);
        await VerificationReporter.WriteAsync(output, plan, request.Json);
        if (request.Quality && !request.Json) await output.WriteLineAsync($"  quality: structural observations --base {request.Base}");
        if (request.PlanOnly)
        {
            return 0;
        }
        string[] limitations = request.Quality ? [Limitation, QualityLimitation] : [Limitation];
        return await EvidenceSession.ExecuteAsync(repository, "verify", plan.Mode, limitations, evidence => request.Quality
            ? RunWithQualityAsync(plan, request.Base!, evidence, cancellationToken)
            : RunPlanAsync(plan, evidence, cancellationToken), notes: output);
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
