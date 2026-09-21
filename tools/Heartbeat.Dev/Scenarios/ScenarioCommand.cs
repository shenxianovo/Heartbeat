using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Parsing;

namespace Heartbeat.Dev;

internal sealed class ScenarioCommand(
    RepositoryContext repository,
    IProcessRunner runner,
    TextWriter output)
{
    public Command CreateCommand()
    {
        var command = new Command("scenario", "Run a reproducible verification scenario");
        var list = new Option<bool>("--list") { Description = "List available scenarios" };
        command.Options.Add(list);
        command.SetAction(async (parse, _) =>
        {
            if (!parse.GetValue(list)) return new HelpAction().Invoke(parse);
            await output.WriteLineAsync(string.Join(Environment.NewLine, command.Subcommands.Select(child => child.Name)));
            return 0;
        });
        AddScenario(command, "replay-fixture", "Replay browser tests with mocked auth and API", native: false);
        AddScenario(command, "hubs-fixture", "Hub management browser tests with mocked auth and API", native: false);
        AddScenario(command, "delivery", "Upload/replay integration tests against PostgreSQL", native: false);
        AddScenario(command, "collector-delivery", "Real macOS Collector through Hub custody to PostgreSQL", native: true);
        AddScenario(command, "desktop-replay", "Interactive packaged desktop to real Web replay", native: true);
        AddScenario(command, "native-desktop", "Guided macOS collector session against an isolated Hub", native: true);
        foreach (var child in command.Subcommands)
            child.Validators.Add(result =>
            {
                if (result.Parent is CommandResult parent
                    && parent.Children.OfType<OptionResult>().Any(option => option.Option == list && !option.Implicit))
                    result.AddError("--list cannot be combined with a scenario name.");
            });
        return command;
    }

    private void AddScenario(Command parent, string name, string description, bool native)
    {
        var command = new Command(name, description);
        var sensitive = new Option<bool>("--include-sensitive-evidence") { Description = "Persist native logs that may contain user context" };
        var keep = new Option<bool>("--keep-environment-on-failure") { Description = "Keep the isolated environment on failure" };
        if (native)
        {
            command.Options.Add(sensitive);
            command.Options.Add(keep);
        }
        command.SetAction((parse, token) => RunAsync(new ScenarioOptions(name, parse.GetValue(sensitive), parse.GetValue(keep)), token));
        parent.Subcommands.Add(command);
    }

    public async Task<int> RunAsync(ScenarioOptions options, CancellationToken cancellationToken)
    {
        return options.Name switch
        {
            "replay-fixture" => await RunBrowserFixtureAsync(options, "replay.spec.ts", cancellationToken),
            "hubs-fixture" => await RunBrowserFixtureAsync(options, "hubs.spec.ts", cancellationToken),
            "delivery" => await RunDeliveryAsync(options, cancellationToken),
            "collector-delivery" => await new CollectorDeliveryScenario(repository, runner, output)
                .RunAsync(options, cancellationToken),
            "desktop-replay" => await new DesktopReplayScenario(repository, runner, output)
                .RunAsync(options, cancellationToken),
            "native-desktop" => await new NativeDesktopScenario(repository, runner, output)
                .RunAsync(options, cancellationToken),
            _ => throw new CommandUsageException($"Unknown scenario '{options.Name}'."),
        };
    }

    private Task<int> RunBrowserFixtureAsync(ScenarioOptions options, string spec, CancellationToken cancellationToken)
    {
        var web = repository.Path("src", "Frontend", "Heartbeat.Web");
        return RunAutomatedAsync(
            options, options.Name, "npm",
            ["--prefix", web, "run", "test:e2e", "--", spec],
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
                "--filter-class", "Heartbeat.Integration.Tests.RecordUploadHttpTests",
                "Heartbeat.Integration.Tests.RecordReplayHttpTests",
                "--report-xunit-trx", "--report-xunit-trx-filename", "delivery.trx",
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

internal sealed record ScenarioOptions(string Name, bool IncludeSensitiveEvidence = false, bool KeepEnvironmentOnFailure = false);
