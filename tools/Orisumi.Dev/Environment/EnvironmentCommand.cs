using System.CommandLine;
using System.CommandLine.Help;
using System.Text.Json;
using Spectre.Console;

namespace Orisumi.Dev;

internal sealed class EnvironmentCommand(RepositoryContext repository, IProcessRunner runner, TextWriter output)
{
    private readonly IAnsiConsole console = Terminal.Create(output);
    private EnvironmentManager? manager;

    // 提示走同一个控制台，转圈动画运行时也能正常显示。
    private EnvironmentManager Manager => manager ??= new(repository, runner, output,
        message => console.MarkupLine($"{Terminal.Mark(Outcome.Attention)} {Terminal.Esc(message)}"));

    public Command CreateCommand()
    {
        var command = new Command("env", "Manage this checkout's environment and machine-wide database volumes");
        command.SetAction(parse => new HelpAction().Invoke(parse));
        var up = new Command("up", "Start selected services and wait until ready");
        var services = new Argument<string[]>("services") { Arity = ArgumentArity.ZeroOrMore };
        services.AcceptOnlyFromAmong("api", "docs", "web");
        services.Description = "Services to start with their dependencies; all when omitted";
        var temporary = new Option<bool>("--temp") { Description = "Use a temporary database volume, removed on env down" };
        var reset = new Option<bool>("--reset") { Description = "Delete and recreate this checkout's default database volume" };
        up.Options.Add(reset);
        up.Arguments.Add(services);
        up.Options.Add(temporary);
        var upJson = JsonOption();
        up.Options.Add(upJson);
        up.SetAction(async (parse, token) =>
        {
            var json = parse.GetValue(upJson);
            var requested = parse.GetValue(services) ?? [];
            var label = requested.Length == 0 ? "all services" : string.Join(", ", requested);
            try
            {
                var state = await Terminal.SpinAsync(console, $"Starting {Terminal.Esc(label)}",
                    () => Manager.UpAsync(requested, parse.GetValue(temporary), token, reset: parse.GetValue(reset)));
                await WriteStatusAsync(state, json);
            }
            catch (EnvironmentAlreadyRunningException running)
            {
                await WriteStatusAsync(running.Status, json);
                throw;
            }
            return 0;
        });
        command.Subcommands.Add(up);
        var down = new Command("down", "Stop this environment and delete its temporary volume");
        down.SetAction(async (_, token) =>
        {
            var removed = await Terminal.SpinAsync(console, "Stopping", () => Manager.DownAsync(token));
            console.MarkupLine($"{Terminal.Mark(Outcome.Passed)} Environment stopped"
                + (removed is { } volume ? $"{Terminal.Dot}temporary volume {Terminal.Esc(volume)} deleted" : ""));
            return 0;
        });
        command.Subcommands.Add(down);
        var status = new Command("status", "Read actual environment state");
        var json = JsonOption();
        status.Options.Add(json);
        status.SetAction(async (parse, token) =>
        {
            await WriteStatusAsync(await Manager.StatusAsync(token), parse.GetValue(json));
            return 0;
        });
        command.Subcommands.Add(status);
        var logs = new Command("logs", "Read resource console logs");
        var logResource = new Argument<string>("resource") { Description = "Resource name, as listed by env status" };
        var follow = new Option<bool>("--follow") { Description = "Keep printing new log lines" };
        logs.Arguments.Add(logResource);
        logs.Options.Add(follow);
        logs.SetAction(async (parse, token) =>
        {
            var args = new List<string> { "logs", parse.GetValue(logResource)! , "--apphost", Manager.AppHost };
            if (parse.GetValue(follow)) args.Add("--follow");
            return await runner.StreamAsync("aspire", args, output, token);
        });
        command.Subcommands.Add(logs);
        var restart = new Command("restart", "Restart one resource");
        var resource = new Argument<string>("resource") { Description = "Resource name, as listed by env status" };
        restart.Arguments.Add(resource);
        restart.SetAction((parse, token) => ForwardAsync(["resource", parse.GetValue(resource)!, "restart"], token));
        command.Subcommands.Add(restart);
        var telemetry = DeveloperCli.Group("telemetry", "Read telemetry from this checkout's environment");
        foreach (var kind in new[] { "logs", "spans", "traces" })
        {
            var read = new Command(kind, $"Read telemetry {kind}");
            var target = new Argument<string?>("resource")
            {
                Arity = ArgumentArity.ZeroOrOne,
                Description = "Resource name; all resources when omitted",
            };
            read.Arguments.Add(target);
            read.SetAction((parse, token) => ForwardAsync(parse.GetValue(target) is { } name
                ? ["otel", kind, name] : ["otel", kind], token));
            telemetry.Subcommands.Add(read);
        }
        command.Subcommands.Add(telemetry);
        var volumes = DeveloperCli.Group("volumes", "Manage Orisumi database volumes across all checkouts on this machine");
        var volumeManager = new EnvironmentVolumes(Manager);
        var list = new Command("list", "List volumes for all checkouts on this machine");
        var listJson = JsonOption();
        list.Options.Add(listJson);
        list.SetAction(async (parse, token) =>
        {
            var found = await volumeManager.ListAsync(token);
            if (parse.GetValue(listJson)) await WriteJsonAsync(found);
            else WriteVolumes(found);
            return 0;
        });
        volumes.Subcommands.Add(list);
        var prune = new Command("prune", "Preview orphaned volumes across this machine; --apply deletes them");
        var apply = new Option<bool>("--apply") { Description = "Delete volumes whose checkout no longer exists" };
        var pruneJson = JsonOption();
        prune.Options.Add(apply);
        prune.Options.Add(pruneJson);
        prune.SetAction(async (parse, token) =>
        {
            var applied = parse.GetValue(apply);
            var candidates = await volumeManager.PruneAsync(applied, token);
            if (parse.GetValue(pruneJson)) await WriteJsonAsync(candidates);
            else WritePrunedVolumes(candidates, applied);
            return 0;
        });
        volumes.Subcommands.Add(prune);
        command.Subcommands.Add(volumes);
        return command;
    }
    private async Task<int> ForwardAsync(string[] args, CancellationToken token)
    {
        var result = await Manager.AspireAsync(args, token);
        await output.WriteAsync(result.StdOut + result.StdErr);
        return result.ExitCode;
    }
    private static Option<bool> JsonOption() => new("--json") { Description = "Print JSON" };

    private async Task WriteStatusAsync(EnvironmentStatus state, bool json)
    {
        if (json) await WriteJsonAsync(state);
        else WriteStatus(console, repository, state);
    }

    internal static void WriteStatus(IAnsiConsole console, RepositoryContext repository, EnvironmentStatus state)
    {
        var checkout = Terminal.Esc(Terminal.Path(repository, state.Checkout) == "." ? Path.GetFileName(state.Checkout) : state.Checkout);
        if (!state.Running)
        {
            Terminal.Heading(console, "Environment", checkout, Terminal.Verdict(Outcome.Waiting, "stopped"));
            Terminal.Hint(console, "orisumi env up");
            return;
        }
        var resources = state.Services!.Select(service => (Service: service, Outcome: Classify(service))).ToArray();
        var problems = resources.Where(item => item.Outcome is Outcome.Failed or Outcome.Attention or Outcome.Waiting).ToArray();
        Terminal.Heading(console, "Environment", checkout, problems.Length == 0
            ? Terminal.Verdict(Outcome.Passed, "running, all healthy")
            : Terminal.Verdict(Outcome.Attention, $"running, {problems.Length} need attention"));
        string Items(bool finished) => string.Join("   ", resources
            .Where(item => IsFinished(item.Service) == finished)
            .Select(item => $"{Terminal.Mark(item.Outcome)} {Terminal.Esc(item.Service.Name)}"));
        var entries = new[] { ("API", state.Entries!.Api), ("Web", state.Entries.Web), ("Docs", state.Entries.Docs), ("Dashboard", state.Dashboard ?? "") };
        var rows = new List<(string, string)>
        {
            ("Services", Items(finished: false)),
            ("Finished", Items(finished: true)),
            ("Entries", string.Join('\n', entries.Select(entry => $"[grey]{entry.Item1,-10}[/] {Terminal.Esc(entry.Item2)}"))),
            ("Database", $"{Terminal.Esc(state.Database!.ConnectionString)}\n[grey]volume[/] {Terminal.Esc(state.Database.Volume)} "
                + $"[grey]({(state.Database.Temporary ? "temporary" : "default")})[/]"),
        };
        Terminal.Fields(console, rows.Where(row => row.Item2.Length > 0));
        foreach (var (service, outcome) in problems)
        {
            console.WriteLine();
            var detail = service.ExitCode is { } code ? $"exited {code}" : service.Health ?? service.State;
            console.MarkupLine($"  {Terminal.Mark(outcome)} {Terminal.Esc(service.Name)} [grey]{Terminal.Esc(detail.ToLowerInvariant())}[/]");
            Terminal.Hint(console, $"orisumi env logs {service.Name}");
        }
    }

    private static bool IsFinished(ServiceStatus service) =>
        service.ExitCode is not null || service.State is "Finished" or "Exited";

    private static Outcome Classify(ServiceStatus service) => service switch
    {
        { ExitCode: 0 } => Outcome.Passed,
        { ExitCode: not null } => Outcome.Failed,
        _ when IsFinished(service) => Outcome.Passed,
        { State: "Running", Health: null or "Healthy" } => Outcome.Running,
        { State: "Running" } => Outcome.Attention,
        { State: "FailedToStart" or "RuntimeUnhealthy" } => Outcome.Failed,
        _ => Outcome.Waiting,
    };

    private void WriteVolumes(IReadOnlyList<EnvironmentVolume> volumes)
    {
        Terminal.Heading(console, "Volumes", $"{volumes.Count} on this machine");
        Terminal.Rows(console, volumes.Select(volume => new[]
        {
            Terminal.Esc(volume.Name),
            volume.Temporary ? "[grey]temporary[/]" : "default",
            (Terminal.Path(repository, volume.Checkout) is "." ? "[grey]this checkout[/]" : Terminal.Esc(Terminal.Path(repository, volume.Checkout)))
                + (volume.Orphaned ? " [yellow](checkout missing)[/]" : ""),
        }));
        if (volumes.Any(volume => volume.Orphaned))
        {
            console.WriteLine();
            Terminal.Hint(console, "orisumi env volumes prune");
        }
    }

    private void WritePrunedVolumes(IReadOnlyList<EnvironmentVolume> candidates, bool applied)
    {
        if (candidates.Count == 0)
        {
            console.MarkupLine($"{Terminal.Mark(Outcome.Passed)} No orphaned volumes");
            return;
        }
        Terminal.Heading(console, "Volumes", applied
            ? Terminal.Verdict(Outcome.Passed, $"deleted {candidates.Count} orphaned")
            : $"{candidates.Count} orphaned, would be deleted");
        Terminal.Rows(console, candidates.Select(volume => new[]
        {
            Terminal.Esc(volume.Name), $"[grey]{Terminal.Esc(volume.Checkout)}[/]",
        }));
        if (!applied)
        {
            console.WriteLine();
            Terminal.Hint(console, "orisumi env volumes prune --apply");
        }
    }

    private Task WriteJsonAsync(object value) => output.WriteLineAsync(JsonSerializer.Serialize(value, JsonOptions.Indented));
}
