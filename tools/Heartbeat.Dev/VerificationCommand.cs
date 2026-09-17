namespace Heartbeat.Dev;

internal sealed class VerificationCommand(
    RepositoryContext repository,
    IProcessRunner runner,
    TextWriter output)
{
    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            await output.WriteLineAsync("""
                Usage: heartbeat-dev verify <changed|full> [--base REF] [--plan] [--json]

                changed requires --base for a clean worktree and otherwise defaults to HEAD.
                Unknown changed paths expand to the full verification plan.
                """);
            return 0;
        }

        var request = await VerificationRequest.ParseAsync(repository, runner, args, cancellationToken);
        var plan = await VerificationPlanner.CreateAsync(repository, runner, request, cancellationToken);
        await VerificationReporter.WriteAsync(output, plan, request.Json);
        if (request.PlanOnly)
        {
            return 0;
        }
        return await EvidenceSession.ExecuteAsync(repository, "verify", plan.Mode,
            ["Browser auth and API responses are mocked; passing checks do not prove the deployed end-to-end chain."],
            async evidence =>
            {
                var run = evidence.Run;
                var commands = evidence.Commands;
                var exitCode = 0;
                await output.WriteLineAsync($"Verification evidence: {run.Directory}");
                foreach (var step in plan.Steps)
                {
                    var environment = EvidenceEnvironment(step, run);
                    var arguments = await WebVerificationWorkspace.ArgumentsAsync(repository, run, step.Arguments, cancellationToken);
                    var command = $"{step.FileName} {string.Join(' ', arguments.Select(Quote))}";
                    commands.Add(command);
                    await output.WriteLineAsync($"Running {step.Name} ...");
                    var result = await runner.CaptureAsync(step.FileName, arguments, environment, cancellationToken);
                    var logName = $"{step.Name}.log";
                    await File.WriteAllTextAsync(
                        Path.Combine(run.Directory, logName), result.StdOut + result.StdErr, cancellationToken);
                    if (result.ExitCode == 0) continue;
                    exitCode = result.ExitCode;
                    await output.WriteAsync(result.StdErr.Length > 0 ? result.StdErr : result.StdOut);
                    break;
                }
                return exitCode;
            }, notes: output);
    }

    private IReadOnlyDictionary<string, string?>? EvidenceEnvironment(VerificationStep step, ArtifactRun run)
    {
        if (step.Name == "browser") return PlaywrightEvidenceEnvironment.Create(run,
            WebVerificationWorkspace.Environment(repository, step.Environment));
        return step.Name == "web" ? WebVerificationWorkspace.Environment(repository, step.Environment) : step.Environment;
    }

    private static string Quote(string value) => value.Contains(' ', StringComparison.Ordinal) ? $"\"{value}\"" : value;
}
