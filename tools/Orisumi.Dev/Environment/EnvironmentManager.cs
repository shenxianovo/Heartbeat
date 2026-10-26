using System.Text.Json;

namespace Orisumi.Dev;

internal sealed class EnvironmentManager(RepositoryContext repository, IProcessRunner runner, TextWriter output)
{
    internal const string CheckoutLabel = "orisumi.checkout";
    internal const string TemporaryLabel = "orisumi.temporary";
    private readonly EnvironmentFiles files = new(repository);
    internal string AppHost => repository.Path("tools", "Orisumi.AppHost", "Orisumi.AppHost.csproj");

    internal async Task<bool> IsWorktreeAsync(CancellationToken token)
    {
        var git = await ExecuteAsync("git", ["rev-parse", "--path-format=absolute", "--git-dir", "--git-common-dir"], token);
        var paths = git.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        return paths[0] != paths[1];
    }

    public async Task<EnvironmentStatus> StatusAsync(CancellationToken token)
    {
        var ps = await ExecuteAsync("aspire", ["ps", "--format", "Json"], token);
        using var hosts = ParseJson(ps.StdOut);
        var host = hosts.RootElement.EnumerateArray().FirstOrDefault(item =>
            item.TryGetProperty("appHostPath", out var path) && Path.GetFullPath(path.GetString()!, repository.Root) == AppHost
            && item.GetProperty("status").GetString() == "running");
        if (host.ValueKind == JsonValueKind.Undefined) return new(repository.Root, false);
        var described = await AspireAsync(["describe", "--format", "Json"], token);
        using var description = ParseJson(described.StdOut);
        var resources = description.RootElement.GetProperty("resources").EnumerateArray().ToArray();
        var services = resources.Where(item => Text(item, "resourceType") is "Project" or "Executable" or "Container").Select(item => new ServiceStatus(Name(item), Text(item, "state") ?? "Unknown",
            Text(item, "healthStatus"), item.TryGetProperty("exitCode", out var exit) && exit.ValueKind == JsonValueKind.Number ? exit.GetInt32() : null)).ToArray();
        var saved = files.Read() ?? files.Defaults();
        var nginx = resources.FirstOrDefault(item => Name(item) == "nginx");
        string Entry(string name, int port, string suffix) => Url(nginx, name) is { } url
            ? url.TrimEnd('/') + suffix : $"http://localhost:{port}{suffix}";
        var db = resources.FirstOrDefault(item => Name(item) == "db");
        var volume = saved.Volume;
        if (db.ValueKind != JsonValueKind.Undefined && db.TryGetProperty("volumes", out var volumes))
            volume = volumes.EnumerateArray().Where(item => Text(item, "mountType") == "volume")
                .Select(item => Text(item, "source")).FirstOrDefault() ?? volume;
        var api = resources.FirstOrDefault(item => Name(item) == "api");
        var connection = api.ValueKind != JsonValueKind.Undefined && api.TryGetProperty("environment", out var environment)
            ? Text(environment, "ConnectionStrings__Orisumi") : null;
        connection ??= $"Host=localhost;Port={saved.Ports.Database};Database=orisumi;Username=orisumi;Password=orisumi";
        return new(repository.Root, true, services,
            new(Entry("api", saved.Ports.Api, "/api"), Entry("docs", saved.Ports.Docs, "/"), Entry("web", saved.Ports.Web, "/")),
            Text(host, "dashboardUrl"), new(connection, volume, saved.Temporary && volume == saved.Volume));
    }

    public async Task<EnvironmentStatus> UpAsync(string[] requested, bool temporary, CancellationToken token,
        string? reuseVolume = null, bool failMigration = false, bool reset = false)
    {
        if (reset && temporary) throw new CommandUsageException("--reset and --temp cannot be used together.");
        var services = ServiceSelection.Resolve(requested);
        var current = await StatusAsync(token);
        if (current.Running)
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(current, JsonOptions.Indented));
            throw new InvalidOperationException("This checkout already has a running environment. Run orisumi env down first.");
        }
        var worktree = await IsWorktreeAsync(token);
        var defaults = files.Defaults();
        var ports = worktree ? EnvironmentPorts.Allocate(files.Read()?.Ports, EnvironmentPorts.Available, EnvironmentPorts.FreePort) : defaults.Ports;
        var volume = reuseVolume ?? (temporary ? EnvironmentFiles.TemporaryVolume() : files.DefaultVolume(worktree));
        if (reset)
        {
            await output.WriteLineAsync($"Resetting default volume '{volume}': all existing data will be deleted.");
            await ExecuteAsync("docker", ["volume", "rm", "--force", volume], token);
        }
        await ExecuteAsync("docker", ["volume", "create", "--label", $"{CheckoutLabel}={repository.Root}",
            "--label", $"{TemporaryLabel}={temporary.ToString().ToLowerInvariant()}", volume], token);
        files.Save(new(ports, volume, temporary));
        var args = new List<string> { "start", "--format", "Json" };
        if (worktree) args.Add("--isolated");
        args.AddRange(["--apphost", AppHost, "--", $"--Services={string.Join(',', requested)}",
            $"--Ports:Api={ports.Api}", $"--Ports:Docs={ports.Docs}", $"--Ports:Web={ports.Web}",
            $"--Ports:Database={ports.Database}", $"--DatabaseVolume={volume}"]);
        if (worktree) args.Add("--AllowedHosts=localhost;127.0.0.1");
        if (failMigration) args.Add("--Testing:FailMigration=true");
        await ExecuteAsync("aspire", args, token);
        // Migration completion is enforced by the api dependency, not by a health check on a finished process.
        foreach (var service in services.Where(service => service != "migrate").Order(StringComparer.Ordinal))
            await AspireAsync(["wait", service, "--timeout", failMigration ? "30" : "240"], token);
        return await StatusAsync(token);
    }

    public async Task DownAsync(CancellationToken token, bool preserveVolume = false)
    {
        var state = files.Read();
        await AspireAsync(["stop", "--force"], token);
        if (!preserveVolume && state is { Temporary: true })
        {
            // aspire stop returns before Docker has removed the database container.
            var containers = await ExecuteAsync("docker", ["ps", "--all", "--quiet", "--filter", $"volume={state.Volume}"], token);
            var ids = containers.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            if (ids.Length > 0) await ExecuteAsync("docker", ["rm", "--force", .. ids], token);
            await ExecuteAsync("docker", ["volume", "rm", "--force", state.Volume], token);
        }
    }

    public Task<ProcessResult> AspireAsync(IReadOnlyList<string> arguments, CancellationToken token) =>
        ExecuteAsync("aspire", [.. arguments, "--apphost", AppHost], token);

    public async Task<ProcessResult> ExecuteAsync(string executable, IReadOnlyList<string> arguments, CancellationToken token)
    {
        var result = await runner.CaptureAsync(executable, arguments, null, token);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"{executable} {string.Join(' ', arguments)} failed ({result.ExitCode}):\n{result.StdOut}{result.StdErr}");
        return result;
    }

    internal static JsonDocument ParseJson(string text)
    {
        // Aspire may print a discovery line before its JSON document.
        var start = text.IndexOfAny(['[', '{']);
        if (start < 0) throw new InvalidOperationException("Aspire returned no JSON.");
        return JsonDocument.Parse(text[start..]);
    }
    private static string Name(JsonElement item) => Text(item, "displayName") ?? Text(item, "name")!;
    private static string? Text(JsonElement item, string name) => item.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string? Url(JsonElement resource, string name) => resource.ValueKind == JsonValueKind.Undefined ? null
        : resource.GetProperty("urls").EnumerateArray().Where(url => Text(url, "name") == name)
            .Select(url => Text(url, "url")).FirstOrDefault();
}
