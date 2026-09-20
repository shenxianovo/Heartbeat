using System.Text.Json;

namespace Heartbeat.Dev;

internal sealed class EnvironmentCommand(
    RepositoryContext repository,
    IProcessRunner runner,
    TextWriter output,
    TextWriter error)
{
    private static readonly string[] RequiredHubVariables =
        ["HEARTBEAT_API_KEY", "HEARTBEAT_OWNER_ID", "HEARTBEAT_HUB_TOKEN"];
    private static readonly string[] ResetContents =
        ["containers", "networks", "postgres-volume", "hub-volume", "development-caches"];

    public const string HelpText = """
        Usage: heartbeat-dev env <action> [options] [services...]

        Actions:
          up       Build and start selected services
          logs     Follow selected service logs
          status   Show selected service status; supports --json
          down     Stop selected services while preserving data
          reset    Preview deletion of all local Docker data; use --apply to execute

        Services: web api db hub desktop
        Options: --release, --env-file PATH, --json, --apply

        desktop packages and opens Heartbeat Dev on macOS with its in-process Hub.
        Use 'env up web desktop' for the local Web/API/database and desktop together.
        Quit Heartbeat Dev before rerunning after code changes; the app runs outside this terminal.
        """;

    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            await output.WriteLineAsync(HelpText);
            return 0;
        }

        var plan = EnvironmentPlan.Parse(args);
        var envFile = ResolveEnvironmentFile(plan.Options.EnvironmentFile);
        EnsureEnvironmentFile(envFile, plan.Options.EnvironmentFile is not null);
        var dotenv = DotenvFile.Read(envFile);
        var compose = ComposeInvocation.Create(repository, envFile, plan.Options.Release);

        return plan.Options.Action switch
        {
            EnvironmentAction.Up => await UpAsync(plan, compose, dotenv, cancellationToken),
            EnvironmentAction.Logs => await runner.RunAsync(
                "docker", [.. compose, "logs", "--follow", .. plan.ComposeServices], null, cancellationToken),
            EnvironmentAction.Status => await StatusAsync(plan, compose, cancellationToken),
            EnvironmentAction.Down => await runner.RunAsync(
                "docker", [.. compose, "rm", "--stop", "--force", .. plan.ComposeServices], null, cancellationToken),
            EnvironmentAction.Reset => await ResetAsync(plan, compose, cancellationToken),
            _ => throw new InvalidOperationException("Unknown environment action."),
        };
    }

    private async Task<int> UpAsync(
        EnvironmentPlan plan,
        IReadOnlyList<string> compose,
        DotenvFile dotenv,
        CancellationToken cancellationToken)
    {
        ValidateUp(plan, dotenv);
        if (plan.ComposeServices.Count == 0)
            return await RunDesktopAsync(cancellationToken);
        var configured = await runner.CaptureAsync(
            "docker", [.. compose, "config", "--quiet"], null, cancellationToken);
        if (configured.ExitCode != 0)
        {
            await error.WriteAsync(configured.StdErr);
            return configured.ExitCode;
        }
        var started = await runner.RunAsync(
            "docker", [.. compose, "up", "--build", "--detach", .. plan.ComposeServices], null, cancellationToken);
        if (started != 0)
        {
            return started;
        }

        await WaitForServicesAsync(plan, compose, dotenv, cancellationToken);
        return plan.RunDesktop
            ? await RunDesktopAsync(cancellationToken)
            : 0;
    }

    private static void ValidateUp(EnvironmentPlan plan, DotenvFile dotenv)
    {
        if (plan.RunDesktop && !OperatingSystem.IsMacOS())
            throw new InvalidOperationException("desktop currently requires macOS.");
        if (!plan.ComposeServices.Contains("hub")) return;
        var missing = RequiredHubVariables.Where(name => string.IsNullOrWhiteSpace(dotenv.Get(name))).ToArray();
        if (missing.Length > 0)
            throw new InvalidOperationException(
                $"Hub configuration is incomplete ({string.Join(',', missing)}). Run ./scripts/setup.sh first.");
    }

    private async Task WaitForServicesAsync(
        EnvironmentPlan plan,
        IReadOnlyList<string> compose,
        DotenvFile dotenv,
        CancellationToken cancellationToken)
    {
        var timeout = ParseTimeout();
        if (plan.ComposeServices.Contains("db"))
        {
            await WaitForDatabaseAsync(compose, timeout, cancellationToken);
        }
        if (plan.ComposeServices.Contains("api"))
        {
            await WaitForHttpAsync(compose, "api", new Uri("http://127.0.0.1:8080/health/ready"), 200, timeout, cancellationToken);
        }
        if (plan.ComposeServices.Contains("web"))
        {
            await WaitForHttpAsync(compose, "web", new Uri("http://127.0.0.1:3000/"), 200, timeout, cancellationToken);
        }
        if (plan.ComposeServices.Contains("hub"))
        {
            var hub = new Uri($"http://127.0.0.1:{GetHubPort(dotenv)}/hub/v1/status");
            await WaitForHttpAsync(compose, "hub", hub, 401, timeout, cancellationToken);
        }
    }

    private async Task<int> RunDesktopAsync(CancellationToken cancellationToken)
    {
        await output.WriteLineAsync("Building Heartbeat Dev. Quit any running development client first to load code changes.");
        var built = await runner.RunAsync("/bin/bash", [repository.Path("scripts", "package-desktop-mac.sh")], null, cancellationToken);
        if (built != 0) return built;
        var application = repository.Path(".artifacts", "desktop", "Heartbeat Dev.app");
        await output.WriteLineAsync($"Opening {application}. Configure the connection in the app; use its menu bar to quit.");
        return await runner.RunAsync("/usr/bin/open", ["-a", application], null, cancellationToken);
    }

    private async Task<int> StatusAsync(
        EnvironmentPlan plan,
        IReadOnlyList<string> compose,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string>(compose) { "ps", "--all" };
        if (plan.Options.Json)
        {
            arguments.Add("--format");
            arguments.Add("json");
        }
        arguments.AddRange(plan.ComposeServices);
        return await runner.RunAsync("docker", arguments, null, cancellationToken);
    }

    private async Task<int> ResetAsync(
        EnvironmentPlan plan,
        IReadOnlyList<string> compose,
        CancellationToken cancellationToken)
    {
        if (!plan.Options.Apply)
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(new
            {
                action = "delete-local-docker-data",
                project = "heartbeat",
                includes = ResetContents,
                applied = false,
                next = "Run heartbeat-dev env reset --apply to execute.",
            }, JsonOptions.Indented));
            return 0;
        }
        return await runner.RunAsync(
            "docker", [.. compose, "down", "--volumes", "--remove-orphans"], null, cancellationToken);
    }

    private async Task WaitForDatabaseAsync(
        IReadOnlyList<string> compose,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        await output.WriteLineAsync("Waiting for db at 127.0.0.1:54329 ...");
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await ThrowIfExitedAsync(compose, "db", cancellationToken);
            var ready = await runner.CaptureAsync(
                "docker", [.. compose, "exec", "--no-TTY", "db", "pg_isready", "-U", "heartbeat", "-d", "heartbeat"], null, cancellationToken);
            if (ready.ExitCode == 0)
            {
                await output.WriteLineAsync("Ready: 127.0.0.1:54329");
                return;
            }
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
        throw new TimeoutException("db did not become ready before the startup timeout.");
    }

    private async Task WaitForHttpAsync(
        IReadOnlyList<string> compose,
        string service,
        Uri uri,
        int expectedStatus,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        await output.WriteLineAsync($"Waiting for {service} at {uri} ...");
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await ThrowIfExitedAsync(compose, service, cancellationToken);
            try
            {
                using var response = await client.GetAsync(uri, cancellationToken);
                if ((int)response.StatusCode == expectedStatus)
                {
                    await output.WriteLineAsync($"Ready: {uri}");
                    return;
                }
            }
            catch (HttpRequestException)
            {
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
        throw new TimeoutException($"{service} did not become ready before the startup timeout.");
    }

    private async Task ThrowIfExitedAsync(
        IReadOnlyList<string> compose,
        string service,
        CancellationToken cancellationToken)
    {
        var id = await runner.CaptureAsync(
            "docker", [.. compose, "ps", "--all", "--quiet", service], null, cancellationToken);
        if (id.ExitCode != 0 || string.IsNullOrWhiteSpace(id.StdOut))
        {
            return;
        }
        var state = await runner.CaptureAsync(
            "docker", ["inspect", "--format", "{{.State.Status}}", id.StdOut.Trim()], null, cancellationToken);
        if (state.StdOut.Trim() is not ("exited" or "dead"))
        {
            return;
        }
        var logs = await runner.CaptureAsync(
            "docker", [.. compose, "logs", "--tail", "40", service], null, cancellationToken);
        throw new InvalidOperationException($"{service} exited during startup. Recent logs:{Environment.NewLine}{logs.StdOut}{logs.StdErr}");
    }

    private string ResolveEnvironmentFile(string? value) => value is null
        ? repository.Path(".env.local")
        : Path.GetFullPath(value, Environment.CurrentDirectory);

    private static void EnsureEnvironmentFile(string path, bool explicitPath)
    {
        if (File.Exists(path))
        {
            return;
        }
        if (explicitPath)
        {
            throw new FileNotFoundException($"Environment file not found: {path}", path);
        }
        using (File.Create(path))
        {
        }
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static TimeSpan ParseTimeout()
    {
        var value = Environment.GetEnvironmentVariable("HEARTBEAT_START_TIMEOUT_SECONDS");
        if (value is null)
        {
            return TimeSpan.FromSeconds(180);
        }
        if (!int.TryParse(value, out var seconds) || seconds <= 0)
        {
            throw new CommandUsageException("HEARTBEAT_START_TIMEOUT_SECONDS must be a positive integer.");
        }
        return TimeSpan.FromSeconds(seconds);
    }

    private static int GetHubPort(DotenvFile dotenv)
    {
        var value = dotenv.Get("HEARTBEAT_HUB_PORT");
        if (string.IsNullOrWhiteSpace(value)) return 4318;
        if (!int.TryParse(value, out var port) || port is < 1 or > 65535)
            throw new CommandUsageException("HEARTBEAT_HUB_PORT must be an integer from 1 through 65535.");
        return port;
    }
}

internal static class JsonOptions
{
    public static readonly JsonSerializerOptions Indented = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
}
