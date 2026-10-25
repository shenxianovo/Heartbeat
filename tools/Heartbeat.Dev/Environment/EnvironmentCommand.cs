using System.CommandLine;
using System.CommandLine.Help;
using System.Text.Json;

namespace Heartbeat.Dev;

internal sealed class EnvironmentCommand(RepositoryContext repository, IProcessRunner runner, TextWriter output)
{
    private readonly EnvironmentManager manager = new(repository, runner, output);

    public Command CreateCommand()
    {
        var command = new Command("env", "Manage the environment for this checkout");
        command.SetAction(parse => new HelpAction().Invoke(parse));
        var up = new Command("up", "Start selected services and wait until ready");
        var services = new Argument<string[]>("services") { Arity = ArgumentArity.ZeroOrMore };
        services.AcceptOnlyFromAmong("api", "docs", "web");
        var temporary = new Option<bool>("--temp");
        var reset = new Option<bool>("--reset");
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
        var logResource = new Argument<string>("service");
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
        var resource = new Argument<string>("service");
        restart.Arguments.Add(resource);
        restart.SetAction((parse, token) => ForwardAsync(["resource", parse.GetValue(resource)!, "restart"], token));
        command.Subcommands.Add(restart);
        var otel = new Command("otel", "Read environment telemetry");
        var kind = new Argument<string>("kind");
        kind.AcceptOnlyFromAmong("logs", "spans", "traces");
        var telemetryResource = new Argument<string?>("service") { Arity = ArgumentArity.ZeroOrOne };
        otel.Arguments.Add(kind);
        otel.Arguments.Add(telemetryResource);
        otel.SetAction((parse, token) => ForwardAsync(parse.GetValue(telemetryResource) is { } name
            ? ["otel", parse.GetValue(kind)!, name] : ["otel", parse.GetValue(kind)!], token));
        command.Subcommands.Add(otel);
        var volumes = new Command("volumes", "List labeled Heartbeat database volumes");
        var volumeManager = new EnvironmentVolumes(manager);
        volumes.SetAction(async (_, token) => { await WriteAsync(await volumeManager.ListAsync(token)); return 0; });
        var prune = new Command("prune", "Preview or remove volumes whose checkout no longer exists");
        var apply = new Option<bool>("--apply");
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
