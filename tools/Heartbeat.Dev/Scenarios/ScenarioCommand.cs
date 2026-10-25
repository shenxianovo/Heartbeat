using System.CommandLine;
using System.Text.Json;

namespace Heartbeat.Dev;

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
        var names = new Argument<string[]>("scenarios") { Arity = ArgumentArity.ZeroOrMore };
        var plan = new Option<bool>("--plan");
        var json = new Option<bool>("--json");
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
        if (plan)
        {
            var checks = new List<object>();
            foreach (var scenario in selected)
            {
                var prerequisite = await scenario.CheckAsync(token);
                checks.Add(new { scenario.Name, prerequisite });
                if (!json) await output.WriteLineAsync($"{scenario.Name}: {(prerequisite.Satisfied ? "ready" : "skip")} — {prerequisite.Reason}");
            }
            if (json) await output.WriteLineAsync(JsonSerializer.Serialize(checks, JsonOptions.Indented));
            return 0;
        }
        return await EvidenceSession.ExecuteAsync(repository, "verify", "scenarios",
            ["Smoke scenarios do not verify browser interaction or hot reload."], async evidence =>
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
                    try
                    {
                        token.ThrowIfCancellationRequested();
                        var prerequisite = await scenario.CheckAsync(token);
                        await log.WriteLineAsync(prerequisite.Reason);
                        if (!prerequisite.Satisfied)
                            results.Add(new(scenario.Name, "skipped", prerequisite.Reason, path));
                        else
                        {
                            if (!json) await output.WriteLineAsync($"Running scenario {scenario.Name} ... Evidence: {path}");
                            await scenario.ExecuteAsync(log, token);
                            results.Add(new(scenario.Name, "passed", null, path));
                        }
                    }
                    catch (Exception exception)
                    {
                        await log.WriteLineAsync(exception.ToString());
                        results.Add(new(scenario.Name, "failed", exception.Message, path));
                        code = exception is OperationCanceledException ? 130 : 1;
                        if (code == 130) break;
                    }
                }
                return code;
            }
            finally
            {
                var report = new { mode = "scenarios", exitCode = code, results, evidence = evidence.Run.Directory };
                var serialized = JsonSerializer.Serialize(report, JsonOptions.Indented);
                await File.WriteAllTextAsync(Path.Combine(evidence.Run.Directory, "verification.json"), serialized, CancellationToken.None);
                if (json) await output.WriteLineAsync(serialized);
                else
                {
                    foreach (var result in results) await output.WriteLineAsync($"{result.Name}: {result.Status} {result.Reason}");
                    await output.WriteLineAsync($"Evidence: {evidence.Run.Directory}");
                }
            }
        }, notes: json ? null : output);
    }
}
