using System.CommandLine;
using System.CommandLine.Help;

namespace Heartbeat.Dev;

internal sealed class VerificationCommand(
    RepositoryContext repository,
    IProcessRunner runner,
    TextWriter output)
{
    public Command CreateCommand()
    {
        var command = new Command("verify", "Verify code and tests; use closeout to include structural quality");
        command.SetAction(parse => new HelpAction().Invoke(parse));
        foreach (var mode in new[] { "changed", "full" })
        {
            var child = new Command(mode, mode == "changed" ? "Check changed paths; a clean worktree requires --base" : "Run all code and test checks, without structural quality");
            var baseRef = new Option<string?>("--base") { Description = "Git comparison base" };
            var plan = new Option<bool>("--plan") { Description = "Print the plan without executing it" };
            var json = new Option<bool>("--json") { Description = "Print the plan as JSON" };
            child.Options.Add(baseRef);
            child.Options.Add(plan);
            child.Options.Add(json);
            child.SetAction((parse, token) => RunAsync(new VerificationRequest(
                mode, parse.GetValue(baseRef), parse.GetValue(plan), parse.GetValue(json)), token));
            command.Subcommands.Add(child);
        }
        command.Subcommands.Add(new CloseoutCommand(repository, runner, output).CreateCommand());
        return command;
    }

    public async Task<int> RunAsync(VerificationRequest request, CancellationToken cancellationToken)
    {
        request = await request.ResolveAsync(runner, cancellationToken);
        var plan = await VerificationPlanner.CreateAsync(repository, runner, request, cancellationToken);
        await VerificationReporter.WriteAsync(output, plan, request.Json);
        if (request.PlanOnly)
        {
            return 0;
        }
        return await EvidenceSession.ExecuteAsync(repository, "verify", plan.Mode,
            ["Container startup and browser interaction are verified separately."],
            evidence => RunPlanAsync(plan, evidence, cancellationToken), notes: output);
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
                var environment = step.Environment;
                var arguments = step.Arguments;
                var command = $"{step.FileName} {string.Join(' ', arguments.Select(Quote))}";
                commands.Add(command);
                await output.WriteLineAsync($"Running {step.Name} ...");
                var log = Path.Combine(run.Directory, $"{step.Name}.log");
                ProcessResult result;
                try
                {
                    result = await runner.CaptureAsync(step.FileName, arguments, environment, cancellationToken);
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
