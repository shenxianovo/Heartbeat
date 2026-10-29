using System.CommandLine;
using System.CommandLine.Help;
using System.Text.Json;

namespace Orisumi.Dev;

internal sealed class EnvironmentCommand(RepositoryContext repository, IProcessRunner runner, TextWriter output)
{
    private readonly EnvironmentManager manager = new(repository, runner, output);

    public Command CreateCommand()
    {
        var command = new Command("env", "Manage this checkout's environment and machine-wide database volumes");
        command.SetAction(parse => new HelpAction().Invoke(parse));
        var up = new Command("up", "Start selected services and wait until ready");
        var services = new Argument<string[]>("services") { Arity = ArgumentArity.ZeroOrMore };
        services.AcceptOnlyFromAmong("api", "docs", "web", "application-identity");
        var temporary = new Option<bool>("--temp") { Description = "Use a temporary database volume, removed on env down" };
        var reset = new Option<bool>("--reset") { Description = "Delete and recreate this checkout's default database volume" };
        up.Options.Add(reset);
        up.Arguments.Add(services);
        up.Options.Add(temporary);
        up.SetAction(async (parse, token) =>
        {
            await WriteAsync(await manager.UpAsync(parse.GetValue(services) ?? [], parse.GetValue(temporary), token, reset: parse.GetValue(reset)));
            return 0;
        });
        command.Subcommands.Add(up);
        var down = new Command("down", "Stop this environment and delete its temporary volume");
        down.SetAction(async (_, token) => { await manager.DownAsync(token); return 0; });
        command.Subcommands.Add(down);
        var status = new Command("status", "Read actual environment state");
        var json = new Option<bool>("--json");
        status.Options.Add(json);
        status.SetAction(async (parse, token) =>
        {
            var state = await manager.StatusAsync(token);
            if (parse.GetValue(json)) await WriteAsync(state);
            else await WriteStatusAsync(state);
            return 0;
        });
        command.Subcommands.Add(status);
        var logs = new Command("logs", "Read resource console logs");
        var logResource = new Argument<string>("resource");
        var follow = new Option<bool>("--follow");
        logs.Arguments.Add(logResource);
        logs.Options.Add(follow);
        logs.SetAction(async (parse, token) =>
        {
            var args = new List<string> { "logs", parse.GetValue(logResource)! , "--apphost", manager.AppHost };
            if (parse.GetValue(follow)) args.Add("--follow");
            return await runner.StreamAsync("aspire", args, output, token);
        });
        command.Subcommands.Add(logs);
        var restart = new Command("restart", "Restart one resource");
        var resource = new Argument<string>("resource");
        restart.Arguments.Add(resource);
        restart.SetAction((parse, token) => ForwardAsync(["resource", parse.GetValue(resource)!, "restart"], token));
        command.Subcommands.Add(restart);
        var telemetry = DeveloperCli.Group("telemetry", "Read telemetry from this checkout's environment");
        foreach (var kind in new[] { "logs", "spans", "traces" })
        {
            var read = new Command(kind, $"Read telemetry {kind}");
            var target = new Argument<string?>("resource") { Arity = ArgumentArity.ZeroOrOne };
            read.Arguments.Add(target);
            read.SetAction((parse, token) => ForwardAsync(parse.GetValue(target) is { } name
                ? ["otel", kind, name] : ["otel", kind], token));
            telemetry.Subcommands.Add(read);
        }
        command.Subcommands.Add(telemetry);
        var volumes = DeveloperCli.Group("volumes", "Manage Orisumi database volumes across all checkouts on this machine");
        var volumeManager = new EnvironmentVolumes(manager);
        var list = new Command("list", "List volumes for all checkouts on this machine");
        list.SetAction(async (_, token) => { await WriteAsync(await volumeManager.ListAsync(token)); return 0; });
        volumes.Subcommands.Add(list);
        var prune = new Command("prune", "Preview orphaned volumes across this machine; --apply deletes them");
        var apply = new Option<bool>("--apply") { Description = "Delete volumes whose checkout no longer exists" };
        prune.Options.Add(apply);
        prune.SetAction(async (parse, token) =>
        {
            await WriteAsync(await volumeManager.PruneAsync(parse.GetValue(apply), token));
            return 0;
        });
        volumes.Subcommands.Add(prune);
        command.Subcommands.Add(volumes);
        return command;
    }
    private async Task<int> ForwardAsync(string[] args, CancellationToken token)
    {
        var result = await manager.AspireAsync(args, token);
        await output.WriteAsync(result.StdOut + result.StdErr);
        return result.ExitCode;
    }
    private async Task WriteStatusAsync(EnvironmentStatus state)
    {
        await output.WriteLineAsync($"Checkout: {state.Checkout}");
        await output.WriteLineAsync($"Environment: {(state.Running ? "Running" : "Stopped")}");
        if (!state.Running) return;
        foreach (var service in state.Services!)
            await output.WriteLineAsync($"  {service.Name}: {service.State}"
                + (service.Health is { } health ? $" ({health})" : "")
                + (service.ExitCode is { } code ? $"; exit {code}" : ""));
        await output.WriteLineAsync($"API: {state.Entries!.Api}");
        await output.WriteLineAsync($"Docs: {state.Entries.Docs}");
        await output.WriteLineAsync($"Web: {state.Entries.Web}");
        await output.WriteLineAsync($"Dashboard: {state.Dashboard}");
        await output.WriteLineAsync($"Database: {state.Database!.ConnectionString}");
        await output.WriteLineAsync($"Volume: {state.Database.Volume} ({(state.Database.Temporary ? "temporary" : "default")})");
    }
    private Task WriteAsync(object value) => output.WriteLineAsync(JsonSerializer.Serialize(value, JsonOptions.Indented));
}
