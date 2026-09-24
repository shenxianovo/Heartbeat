using System.CommandLine;
using System.Text.Json;

namespace Heartbeat.Dev;

internal sealed record CloseoutResult(int Verification, int? Quality)
{
    public int ExitCode => Quality == 130 ? 130 : Verification != 0 ? Verification : Quality ?? 1;
}

internal sealed class CloseoutCommand(RepositoryContext repository, IProcessRunner runner, TextWriter output)
{
    private const string Limitation = "Scenarios, native acceptance and performance benchmarks are selected separately for the changed behavior.";

    public Command CreateCommand()
    {
        var command = new Command("closeout", "Run changed-path verification and structural quality against one explicit Git base");
        var baseRef = new Option<string>("--base") { Required = true, Description = "Git comparison base for this task" };
        var plan = new Option<bool>("--plan") { Description = "Print both checks without executing them" };
        command.Options.Add(baseRef);
        command.Options.Add(plan);
        command.SetAction((parse, token) => RunAsync(parse.GetValue(baseRef)!, parse.GetValue(plan), token));
        return command;
    }

    private async Task<int> RunAsync(string baseRef, bool planOnly, CancellationToken cancellationToken)
    {
        var resolved = await runner.CaptureAsync("git",
            ["rev-parse", "--verify", $"{RewriteLineage.Resolve(baseRef)}^{{commit}}"], null, cancellationToken);
        if (resolved.ExitCode != 0) throw new CommandUsageException($"Invalid Git base '{baseRef}'.");
        var commit = resolved.StdOut.Trim();
        var plan = await VerificationPlanner.CreateAsync(repository, runner,
            new VerificationRequest("changed", commit, false, false), cancellationToken);
        await output.WriteLineAsync($"Closeout base: {baseRef} ({commit})");
        await VerificationReporter.WriteAsync(output, plan, json: false);
        await output.WriteLineAsync($"  quality: structural gate --base {commit}");
        await output.WriteLineAsync($"  Not included: {Limitation}");
        if (planOnly) return 0;

        return await EvidenceSession.ExecuteAsync(repository, "verify", "closeout", [Limitation,
            "Browser auth and API responses are mocked; passing checks do not prove the deployed end-to-end chain."], async evidence =>
        {
            var result = await RunChecksAsync(
                () => new VerificationCommand(repository, runner, output).RunPlanAsync(plan, evidence, cancellationToken),
                () => new QualityCommand(repository, runner, output).RunChecksAsync(new QualityOptions(commit), evidence, cancellationToken),
                cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(evidence.Run.Directory, "closeout.json"),
                JsonSerializer.Serialize(new { requestedBase = baseRef, resolvedBase = commit, result, verification = plan }, JsonOptions.Indented),
                CancellationToken.None);
            await output.WriteLineAsync($"Closeout summary: verification exit {result.Verification}; quality "
                + (result.Quality is { } code ? $"exit {code}" : "not run") + $"; evidence: {evidence.Run.Directory}");
            return result.ExitCode;
        }, notes: output);
    }

    internal static async Task<CloseoutResult> RunChecksAsync(
        Func<Task<int>> verify, Func<Task<int>> quality, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var verification = await verify();
        if (verification == 130) return new CloseoutResult(verification, null);
        cancellationToken.ThrowIfCancellationRequested();
        return new CloseoutResult(verification, await quality());
    }
}
