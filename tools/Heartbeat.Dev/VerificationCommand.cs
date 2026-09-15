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
        var run = new ArtifactStore(repository).Create("verify", plan.Mode);
        var started = DateTimeOffset.UtcNow;
        var commands = new List<string>();
        var artifacts = new List<string>();
        var exitCode = 0;
        await output.WriteLineAsync($"Verification evidence: {run.Directory}");
        foreach (var step in plan.Steps)
        {
            var environment = EvidenceEnvironment(step, run);
            var command = $"{step.FileName} {string.Join(' ', step.Arguments.Select(Quote))}";
            commands.Add(command);
            await output.WriteLineAsync($"Running {step.Name} ...");
            var result = await runner.CaptureAsync(step.FileName, step.Arguments, environment, cancellationToken);
            var logName = $"{step.Name}.log";
            await File.WriteAllTextAsync(
                Path.Combine(run.Directory, logName), result.StdOut + result.StdErr, cancellationToken);
            artifacts.Add(logName);
            if (result.ExitCode == 0) continue;
            exitCode = result.ExitCode;
            await output.WriteAsync(result.StdErr.Length > 0 ? result.StdErr : result.StdOut);
            break;
        }
        artifacts.AddRange(Directory.EnumerateFileSystemEntries(run.Directory)
            .Select(Path.GetFileName)
            .Where(item => item is not null and not "manifest.json" && !artifacts.Contains(item, StringComparer.Ordinal))
            .Select(item => item!)
            .Order(StringComparer.Ordinal));
        await ArtifactStore.WriteManifestAsync(run, new EvidenceManifest(
            run.Id, "verify", plan.Mode, started, DateTimeOffset.UtcNow, exitCode, false,
            commands, artifacts,
            ["Browser artifacts are retained on failure; passing Playwright runs may produce only their command log and JSON report."]),
            cancellationToken);
        return exitCode;
    }

    private static IReadOnlyDictionary<string, string?>? EvidenceEnvironment(VerificationStep step, ArtifactRun run)
    {
        if (step.Name != "browser") return step.Environment;
        return PlaywrightEvidenceEnvironment.Create(run, step.Environment);
    }

    private static string Quote(string value) => value.Contains(' ', StringComparison.Ordinal) ? $"\"{value}\"" : value;
}
