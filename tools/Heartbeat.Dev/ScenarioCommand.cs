namespace Heartbeat.Dev;

internal sealed class ScenarioCommand(
    RepositoryContext repository,
    IProcessRunner runner,
    TextWriter output)
{
    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            await output.WriteLineAsync("""
                Usage: heartbeat-dev scenario <replay-fixture|delivery|native-desktop> [options]

                Use scenario --list to print the available names.

                replay-fixture  Re-runs the replay browser test against mocked auth and API, keeping evidence
                delivery        Re-runs the record upload/replay integration tests against PostgreSQL, keeping evidence
                native-desktop  Guided macOS collector session against an isolated local Hub

                replay-fixture and delivery re-run a subset of the first verification layer;
                only native-desktop runs processes and a database that no test harness stands in for.

                Options:
                  --include-sensitive-evidence  Persist native logs that may contain user context
                  --keep-environment-on-failure Keep an isolated native Hub for diagnosis
                """);
            return 0;
        }

        if (args.Length == 1 && args[0] == "--list")
        {
            await output.WriteLineAsync("replay-fixture\ndelivery\nnative-desktop");
            return 0;
        }

        var options = ScenarioOptions.Parse(args);
        return options.Name switch
        {
            "replay-fixture" => await RunReplayFixtureAsync(options, cancellationToken),
            "delivery" => await RunDeliveryAsync(options, cancellationToken),
            "native-desktop" => await new NativeDesktopScenario(repository, runner, output)
                .RunAsync(options, cancellationToken),
            _ => throw new CommandUsageException($"Unknown scenario '{options.Name}'."),
        };
    }

    private Task<int> RunReplayFixtureAsync(ScenarioOptions options, CancellationToken cancellationToken)
    {
        var web = repository.Path("src", "Frontend", "Heartbeat.Web");
        return RunAutomatedAsync(
            options, "replay-fixture", "npm",
            ["--prefix", web, "run", "test:e2e", "--", "replay.spec.ts"],
            run => PlaywrightEvidenceEnvironment.Create(run, WebVerificationWorkspace.Environment(repository)),
            [
                "This re-runs a subset of the existing browser tests and keeps their evidence; it is not an independent scenario.",
                "Browser auth and API responses are mocked; this does not prove the deployed end-to-end chain.",
            ],
            cancellationToken);
    }

    private Task<int> RunDeliveryAsync(ScenarioOptions options, CancellationToken cancellationToken) =>
        RunAutomatedAsync(
            options, "delivery", "dotnet",
            [
                "test", repository.Path("tests", "Heartbeat.Integration.Tests", "Heartbeat.Integration.Tests.csproj"),
                "--filter", "FullyQualifiedName~RecordUploadHttpTests|FullyQualifiedName~RecordReplayHttpTests",
                "--logger", "trx;LogFileName=delivery.trx",
                "--results-directory", "{artifact}", "--verbosity", "minimal",
            ],
            _ => null,
            [
                "This re-runs a subset of the existing integration tests and keeps their evidence; it is not an independent scenario.",
                "Exercises API and PostgreSQL integration; it does not run Web, Hub, and Collector as one real chain.",
            ],
            cancellationToken);

    private async Task<int> RunAutomatedAsync(
        ScenarioOptions options,
        string name,
        string fileName,
        IReadOnlyList<string> argumentTemplate,
        Func<ArtifactRun, IReadOnlyDictionary<string, string?>?> environment,
        IReadOnlyList<string> limitations,
        CancellationToken cancellationToken)
    {
        return await EvidenceSession.ExecuteAsync(repository, "scenario", name, limitations, async evidence =>
        {
            var run = evidence.Run;
            var arguments = await WebVerificationWorkspace.ArgumentsAsync(repository, run,
                argumentTemplate.Select(value => value == "{artifact}" ? run.Directory : value).ToArray(), cancellationToken);
            var command = $"{fileName} {string.Join(' ', arguments.Select(Quote))}";
            evidence.Commands.Add(command);
            await output.WriteLineAsync($"Scenario evidence: {run.Directory}");
            var result = await runner.CaptureAsync(fileName, arguments, environment(run), cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(run.Directory, "command.log"), result.StdOut + result.StdErr, cancellationToken);
            if (result.ExitCode != 0) await output.WriteLineAsync(result.StdErr.Length > 0 ? result.StdErr : result.StdOut);
            return result.ExitCode;
        }, options.IncludeSensitiveEvidence, output);
    }

    private static string Quote(string value) => value.Contains(' ', StringComparison.Ordinal) ? $"\"{value}\"" : value;
}

internal sealed record ScenarioOptions(string Name, bool IncludeSensitiveEvidence, bool KeepEnvironmentOnFailure)
{
    public static ScenarioOptions Parse(IReadOnlyList<string> args)
    {
        var sensitive = false;
        var keep = false;
        foreach (var argument in args.Skip(1))
        {
            switch (argument)
            {
                case "--include-sensitive-evidence": sensitive = true; break;
                case "--keep-environment-on-failure": keep = true; break;
                default: throw new CommandUsageException($"Unknown scenario option '{argument}'.");
            }
        }
        if (args[0] != "native-desktop" && (sensitive || keep))
        {
            throw new CommandUsageException(
                "--include-sensitive-evidence and --keep-environment-on-failure apply only to native-desktop.");
        }
        return new ScenarioOptions(args[0], sensitive, keep);
    }
}
