using System.CommandLine;
using System.CommandLine.Help;
using System.Text.Json;

namespace Heartbeat.Dev;

internal sealed class EnvironmentCommand(
    RepositoryContext repository, IProcessRunner runner, TextWriter output, TextWriter error)
{
    public Command CreateCommand()
    {
        var command = new Command("env", "Start, inspect, stop, or reset the local Compose environment");
        command.SetAction(parse => new HelpAction().Invoke(parse));
        foreach (var action in Enum.GetValues<EnvironmentAction>()) command.Subcommands.Add(CreateAction(action));
        return command;
    }

    private Command CreateAction(EnvironmentAction action)
    {
        var command = new Command(action.ToString().ToLowerInvariant(), action switch
        {
            EnvironmentAction.Up => "Build selected stacks and wait for their public entry points",
            EnvironmentAction.Down => "Stop selected applications, preserving data and unselected shared services",
            EnvironmentAction.Status => "Show service status",
            EnvironmentAction.Logs => "Follow service logs",
            _ => "Preview removal of this Compose environment and its named volumes; --apply executes",
        });
        var targets = new Argument<string[]>("targets")
        {
            Arity = ArgumentArity.ZeroOrMore,
            Description = "all api docs db (default: all)",
        };
        targets.AcceptOnlyFromAmong(EnvironmentPlan.AllowedServices);
        if (action != EnvironmentAction.Reset) command.Arguments.Add(targets);
        var envFile = new Option<string?>("--env-file") { Description = "Optional Compose environment file" };
        var release = new Option<bool>("--release") { Description = "Use the local production Compose configuration" };
        var json = new Option<bool>("--json") { Description = "Print JSON status" };
        var apply = new Option<bool>("--apply") { Description = "Delete this environment and its named volumes" };
        command.Options.Add(envFile);
        command.Options.Add(release);
        if (action == EnvironmentAction.Status) command.Options.Add(json);
        if (action == EnvironmentAction.Reset) command.Options.Add(apply);
        command.SetAction((parse, token) => RunAsync(EnvironmentPlan.Create(new EnvironmentOptions(
            action, parse.GetValue(release), parse.GetValue(envFile), parse.GetValue(json), parse.GetValue(apply),
            new HashSet<string>(parse.GetValue(targets) ?? [], StringComparer.OrdinalIgnoreCase))), token));
        return command;
    }

    public async Task<int> RunAsync(EnvironmentPlan plan, CancellationToken cancellationToken)
    {
        var envFile = plan.Options.EnvironmentFile is { } value ? Path.GetFullPath(value) : null;
        if (envFile is not null && !File.Exists(envFile))
            throw new CommandUsageException($"Environment file not found: {envFile}");
        var compose = ComposeInvocation.Create(repository, envFile, plan.Options.Release);
        switch (plan.Options.Action)
        {
            case EnvironmentAction.Up:
                return await UpAsync(plan, compose, cancellationToken);
            case EnvironmentAction.Logs:
                return await runner.RunAsync("docker", [.. compose, "logs", "--follow", .. plan.ComposeServices], null, cancellationToken);
            case EnvironmentAction.Status:
                var status = new List<string>(compose) { "ps", "--all" };
                if (plan.Options.Json) { status.Add("--format"); status.Add("json"); }
                status.AddRange(plan.ComposeServices);
                return await runner.RunAsync("docker", status, null, cancellationToken);
            case EnvironmentAction.Down:
                return plan.WholeStack
                    ? await runner.RunAsync("docker", [.. compose, "down"], null, cancellationToken)
                    : await runner.RunAsync("docker", [.. compose, "rm", "--stop", "--force", .. plan.ComposeServices], null, cancellationToken);
            case EnvironmentAction.Reset:
                if (!plan.Options.Apply)
                {
                    await output.WriteLineAsync(JsonSerializer.Serialize(new
                    {
                        action = "delete-compose-environment-and-named-volumes", applied = false,
                        command = string.Join(' ', (string[])["docker", .. compose, "down", "--volumes", "--remove-orphans"]),
                    }, JsonOptions.Indented));
                    return 0;
                }
                return await runner.RunAsync("docker", [.. compose, "down", "--volumes", "--remove-orphans"], null, cancellationToken);
            default:
                throw new InvalidOperationException("Unknown environment action.");
        }
    }

    private async Task<int> UpAsync(EnvironmentPlan plan, IReadOnlyList<string> compose, CancellationToken token)
    {
        var timeout = StartupTimeout();
        var configured = await runner.CaptureAsync("docker", [.. compose, "config", "--quiet"], null, token);
        if (configured.ExitCode != 0)
        {
            await error.WriteAsync(configured.StdErr);
            return configured.ExitCode;
        }
        var started = await runner.RunAsync("docker", [.. compose, "up", "--build", "--detach", .. plan.ComposeServices], null, token);
        if (started != 0) return started;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        try
        {
            var targets = plan.WholeStack ? new HashSet<string>(["api", "docs", "db"]) : plan.Options.RequestedServices;
            var config = await runner.CaptureAsync("docker", [.. compose, "config", "--format", "json"], null, deadline.Token);
            if (config.ExitCode != 0) throw new InvalidOperationException(config.StdErr.Trim());
            using var document = JsonDocument.Parse(config.StdOut);
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var services = document.RootElement.GetProperty("services");
            if (targets.Contains("api") || targets.Contains("db"))
                await WaitForAsync("database", () => DatabaseReadyAsync(compose, deadline.Token), deadline.Token);
            if (targets.Contains("api"))
            {
                var port = PublishedPort(services, "nginx", 80);
                var id = Guid.CreateVersion7();
                await WaitForAsync("API", () => ApiReadyAsync(http, port, id, deadline.Token), deadline.Token);
            }
            if (targets.Contains("docs"))
            {
                var port = PublishedPort(services, "nginx", 3000);
                await WaitForAsync("documentation", async () =>
                {
                    using var response = await http.GetAsync($"http://127.0.0.1:{port}/core", deadline.Token);
                    return response.IsSuccessStatusCode;
                }, deadline.Token);
            }
            return 0;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !token.IsCancellationRequested)
        {
            var timedOut = exception is OperationCanceledException;
            await error.WriteLineAsync(timedOut ? $"Startup timed out after {timeout.TotalSeconds:0} seconds." : exception.Message);
            var logs = await runner.CaptureAsync("docker", [.. compose, "logs", "--tail", "40"], null, token);
            await error.WriteAsync(logs.StdOut + logs.StdErr);
            return 1;
        }
    }

    private async Task<bool> DatabaseReadyAsync(IReadOnlyList<string> compose, CancellationToken token)
    {
        var result = await runner.CaptureAsync("docker",
            [.. compose, "exec", "--no-TTY", "db", "pg_isready", "-U", "heartbeat", "-d", "heartbeat"], null, token);
        return result.ExitCode == 0;
    }

    private static async Task<bool> ApiReadyAsync(HttpClient http, int port, Guid id, CancellationToken token)
    {
        using var response = await http.GetAsync($"http://127.0.0.1:{port}/api/entities/{id}", token);
        if (response.IsSuccessStatusCode) return true;
        if (response.StatusCode != System.Net.HttpStatusCode.NotFound) return false;
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        return body.RootElement.TryGetProperty("detail", out var detail)
            && detail.GetString() == "The entity does not exist.";
    }

    private async Task WaitForAsync(string name, Func<Task<bool>> check, CancellationToken token)
    {
        await output.WriteLineAsync($"Waiting for {name} ...");
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (await check())
                {
                    await output.WriteLineAsync($"Ready: {name}");
                    return;
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or JsonException
                || exception is OperationCanceledException && !token.IsCancellationRequested)
            {
                // A restarting upstream can briefly be unreachable or return an incomplete response.
            }
            await Task.Delay(TimeSpan.FromSeconds(1), token);
        }
    }

    private static int PublishedPort(JsonElement services, string service, int target)
    {
        foreach (var port in services.GetProperty(service).GetProperty("ports").EnumerateArray())
            if (port.GetProperty("target").GetInt32() == target
                && int.TryParse(port.GetProperty("published").ToString(), out var published)) return published;
        throw new InvalidOperationException($"No published port for {service}:{target}.");
    }

    private static TimeSpan StartupTimeout()
    {
        var value = Environment.GetEnvironmentVariable("HEARTBEAT_START_TIMEOUT_SECONDS");
        if (value is null) return TimeSpan.FromSeconds(180);
        if (!int.TryParse(value, out var seconds) || seconds <= 0)
            throw new CommandUsageException("HEARTBEAT_START_TIMEOUT_SECONDS must be a positive integer.");
        return TimeSpan.FromSeconds(seconds);
    }
}

internal static class JsonOptions
{
    public static readonly JsonSerializerOptions Indented = new(JsonSerializerDefaults.Web) { WriteIndented = true };
}
