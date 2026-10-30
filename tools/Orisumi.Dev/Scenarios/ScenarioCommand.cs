using System.CommandLine;
using System.Diagnostics;
using System.Text.Json;
using Spectre.Console;

namespace Orisumi.Dev;

internal sealed record ScenarioPrerequisite(bool Satisfied, string Reason);
internal interface ISmokeScenario
{
    string Name { get; }
    Task<ScenarioPrerequisite> CheckAsync(CancellationToken token);
    Task ExecuteAsync(TextWriter log, CancellationToken token);
}
internal sealed record ScenarioResult(string Name, string Status, string? Reason, string Log);

internal sealed class ScenarioCommand(RepositoryContext repository, IProcessRunner runner, TextWriter output,
    IReadOnlyList<ISmokeScenario>? scenarios = null)
{
    private IReadOnlyList<ISmokeScenario> Scenarios => scenarios ?? [
        new EnvironmentScenario(repository, runner), new ApiScenario(new EnvironmentManager(repository, runner, output))];

    public Command CreateCommand()
    {
        var command = new Command("scenarios", "Run smoke scenarios with recorded evidence");
        var names = new Argument<string[]>("scenarios")
        {
            Arity = ArgumentArity.ZeroOrMore,
            Description = $"Scenarios to run: {string.Join(", ", Scenarios.Select(scenario => scenario.Name))}; all when omitted",
        };
        var plan = new Option<bool>("--plan") { Description = "Check prerequisites without running scenarios" };
        var json = new Option<bool>("--json") { Description = "Print the plan or results as JSON" };
        command.Arguments.Add(names);
        command.Options.Add(plan);
        command.Options.Add(json);
        command.SetAction((parse, token) => RunAsync(parse.GetValue(names) ?? [], parse.GetValue(plan), parse.GetValue(json), token));
        return command;
    }

    internal async Task<int> RunAsync(string[] names, bool plan, bool json, CancellationToken token)
    {
        var available = Scenarios;
        foreach (var name in names)
            if (!available.Any(scenario => scenario.Name == name)) throw new CommandUsageException($"Unknown scenario '{name}'.");
        var selected = available.Where(scenario => names.Length == 0 || names.Contains(scenario.Name)).ToArray();
        var console = Terminal.Create(output);
        var width = selected.Select(scenario => scenario.Name.Length).DefaultIfEmpty(0).Max();
        if (plan)
        {
            var checks = new List<object>();
            var rows = new List<string[]>();
            foreach (var scenario in selected)
            {
                var prerequisite = await scenario.CheckAsync(token);
                checks.Add(new { scenario.Name, prerequisite });
                rows.Add([
                    $"{Terminal.Mark(prerequisite.Satisfied ? Outcome.Passed : Outcome.Skipped)} {Terminal.Esc(scenario.Name)}",
                    prerequisite.Satisfied ? "[green]ready[/]" : "[grey]skip[/]",
                    $"[grey]{Terminal.Esc(prerequisite.Reason)}[/]",
                ]);
            }
            if (json) await output.WriteLineAsync(JsonSerializer.Serialize(checks, JsonOptions.Indented));
            else
            {
                Terminal.Heading(console, "Scenarios plan", $"{selected.Length} selected");
                Terminal.Rows(console, rows);
            }
            return 0;
        }
        if (!json) Terminal.Heading(console, "Scenarios", $"{selected.Length} selected");
        var started = Stopwatch.StartNew();
        string? directory = null;
        var exitCode = 1;
        try
        {
            exitCode = await EvidenceSession.ExecuteAsync(repository, "verify", "scenarios",
                ["Smoke scenarios do not verify browser interaction or hot reload."], evidence =>
                {
                    directory = evidence.Run.Directory;
                    return RunScenariosAsync(selected, evidence, json, console, width, token);
                }, notes: json ? null : output);
            return exitCode;
        }
        catch (OperationCanceledException)
        {
            exitCode = 130;
            throw;
        }
        finally
        {
            if (!json && directory is not null)
                VerificationReporter.WriteVerdict(console, repository, exitCode, started.Elapsed, directory);
        }
    }

    private async Task<int> RunScenariosAsync(
        IReadOnlyList<ISmokeScenario> selected, EvidenceSession evidence, bool json, IAnsiConsole console, int width,
        CancellationToken token)
    {
        var results = new List<ScenarioResult>();
        var code = 0;
        try
        {
            foreach (var scenario in selected)
            {
                var path = Path.Combine(evidence.Run.Directory, $"scenario-{scenario.Name}.log");
                await using var log = new StreamWriter(path) { AutoFlush = true };
                evidence.Commands.Add($"scenario {scenario.Name}");
                var clock = Stopwatch.StartNew();
                ScenarioResult result;
                try
                {
                    token.ThrowIfCancellationRequested();
                    var prerequisite = await scenario.CheckAsync(token);
                    await log.WriteLineAsync(prerequisite.Reason);
                    if (!prerequisite.Satisfied) result = new(scenario.Name, "skipped", prerequisite.Reason, path);
                    else
                    {
                        if (json) await scenario.ExecuteAsync(log, token);
                        else await Terminal.SpinAsync(console, $"Running {Terminal.Esc(scenario.Name)}", () => scenario.ExecuteAsync(log, token));
                        result = new(scenario.Name, "passed", null, path);
                    }
                }
                catch (Exception exception)
                {
                    await log.WriteLineAsync(exception.ToString());
                    result = new(scenario.Name, "failed", exception.Message, path);
                    code = exception is OperationCanceledException ? 130 : 1;
                }
                results.Add(result);
                if (!json) WriteResult(console, result, width, clock.Elapsed, code == 130);
                if (code == 130) break;
            }
            return code;
        }
        finally
        {
            var report = new { mode = "scenarios", exitCode = code, results, evidence = evidence.Run.Directory };
            var serialized = JsonSerializer.Serialize(report, JsonOptions.Indented);
            await File.WriteAllTextAsync(Path.Combine(evidence.Run.Directory, "verification.json"), serialized, CancellationToken.None);
            if (json) await output.WriteLineAsync(serialized);
        }
    }

    private void WriteResult(IAnsiConsole console, ScenarioResult result, int width, TimeSpan elapsed, bool cancelled)
    {
        var outcome = result.Status switch
        {
            "passed" => Outcome.Passed,
            "skipped" => Outcome.Skipped,
            _ => cancelled ? Outcome.Cancelled : Outcome.Failed,
        };
        VerificationReporter.WriteStep(console, result.Name, width, outcome, elapsed,
            result.Reason is { } reason ? $"[grey]{Terminal.Esc(reason)}[/]" : "");
        if (outcome == Outcome.Failed)
            console.MarkupLine($"      [grey]log[/] {Terminal.Esc(Terminal.Path(repository, result.Log))}");
    }
}
