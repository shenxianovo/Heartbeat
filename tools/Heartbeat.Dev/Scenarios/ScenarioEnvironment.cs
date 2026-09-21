using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace Heartbeat.Dev;

// Shared local resources, not a catalogue of test scopes. Each scenario chooses what to start
// and connects implementations through their existing interfaces.
internal sealed class ScenarioEnvironment(
    IProcessRunner runner, EvidenceSession evidence, IReadOnlyList<string> compose,
    IReadOnlyDictionary<string, string?> variables, string projectName, Uri hub, Uri web, string token, Guid owner)
{
    public EvidenceSession Evidence { get; } = evidence;
    public string ProjectName { get; } = projectName;
    public Uri Hub { get; } = hub;
    public string Token { get; } = token;
    public Guid Owner { get; } = owner;
    public Uri Web { get; } = web;

    public static Task<int> RunAsync(RepositoryContext repository, IProcessRunner runner, TextWriter output,
        ScenarioOptions options, IReadOnlyList<string> limitations, Func<ScenarioEnvironment, Task<int>> scenario,
        CancellationToken cancellationToken)
    {
        var notes = limitations.ToList();
        return EvidenceSession.ExecuteAsync(repository, "scenario", options.Name, notes, async evidence =>
        {
            var environment = await CreateAsync(repository, runner, evidence, cancellationToken);
            await output.WriteLineAsync($"Scenario evidence: {evidence.Run.Directory}");
            var exitCode = 1;
            var removed = false;
            try
            {
                exitCode = await scenario(environment);
            }
            finally
            {
                if (exitCode != 0 && options.KeepEnvironmentOnFailure)
                {
                    notes.Add($"The failed environment was retained as Docker Compose project {environment.ProjectName}.");
                    await output.WriteLineAsync($"Retained failed environment: {environment.ProjectName}");
                }
                else
                {
                    removed = await environment.RemoveAsync();
                    notes.Add(removed ? "The isolated Docker environment and volumes were removed."
                        : $"Cleanup was not confirmed for Docker Compose project {environment.ProjectName}.");
                    if (!removed) await output.WriteLineAsync($"Cleanup was not confirmed for {environment.ProjectName}.");
                }
            }
            return exitCode == 0 && !removed ? 1 : exitCode;
        }, options.IncludeSensitiveEvidence, output);
    }

    private static async Task<ScenarioEnvironment> CreateAsync(RepositoryContext repository, IProcessRunner runner,
        EvidenceSession evidence, CancellationToken cancellationToken)
    {
        var dotenv = DotenvFile.Read(repository.Path(".env.local"));
        var owner = Guid.Parse(dotenv.Get("HEARTBEAT_OWNER_ID") ?? throw new InvalidOperationException("Run dotnet run --project tools/Heartbeat.Dev -- env setup first."));
        if (string.IsNullOrWhiteSpace(dotenv.Get("HEARTBEAT_API_KEY")))
            throw new InvalidOperationException("Run dotnet run --project tools/Heartbeat.Dev -- env setup first to configure an Auth API key.");
        var project = $"heartbeat-scenario-{Guid.NewGuid():N}";
        var port = TcpPort.Reserve();
        var webPort = TcpPort.Reserve();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var overridePath = Path.Combine(evidence.Run.Directory, "compose.override.yaml");
        await File.WriteAllTextAsync(overridePath, """
            services:
              db:
                ports: !reset []
              api:
                ports: !reset []
              web:
                ports: !override
                  - "127.0.0.1:${HEARTBEAT_SCENARIO_WEB_PORT}:3000"
              hub:
                environment:
                  Hub__UploadIntervalSeconds: 1
            """ + Environment.NewLine, cancellationToken);
        var variables = new Dictionary<string, string?>
        {
            ["HEARTBEAT_HUB_PORT"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["HEARTBEAT_SCENARIO_WEB_PORT"] = webPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["HEARTBEAT_HUB_TOKEN"] = token,
            ["HEARTBEAT_OWNER_ID"] = owner.ToString(),
            ["HEARTBEAT_API_KEY"] = dotenv.Get("HEARTBEAT_API_KEY"),
        };
        return new ScenarioEnvironment(runner, evidence,
            [.. ComposeInvocation.Create(repository, repository.Path(".env.local"), release: true, project), "--file", overridePath],
            variables, project, new Uri($"http://127.0.0.1:{port}/"), new Uri($"http://127.0.0.1:{webPort}/"), token, owner);
    }

    public Task StartAsync(CancellationToken cancellationToken, params string[] services) =>
        ComposeAsync($"start-{string.Join('-', services)}", ["up", "--build", "--detach", .. services], cancellationToken);

    public Task InitializeDatabaseAsync(CancellationToken cancellationToken) =>
        ComposeAsync("migrate", ["run", "--rm", "--build", "migrate"], cancellationToken);

    public HttpClient ConnectHub()
    {
        var client = new HttpClient { BaseAddress = Hub, Timeout = TimeSpan.FromSeconds(2) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return client;
    }

    public Task WriteAsync(string name, object value, CancellationToken cancellationToken) =>
        File.WriteAllTextAsync(Path.Combine(Evidence.Run.Directory, name),
            JsonSerializer.Serialize(value, JsonOptions.Indented) + Environment.NewLine, cancellationToken);

    public async Task<string> QueryDatabaseAsync(string query, CancellationToken cancellationToken, string evidenceName = "database-check.sql")
    {
        await File.WriteAllTextAsync(Path.Combine(Evidence.Run.Directory, evidenceName), query + Environment.NewLine, cancellationToken);
        var command = $"docker compose --project-name {ProjectName} exec --no-TTY db psql ({evidenceName})";
        if (!Evidence.Commands.Contains(command)) Evidence.Commands.Add(command);
        var result = await runner.CaptureAsync("docker", [.. compose, "exec", "--no-TTY", "db", "psql", "-U", "heartbeat",
            "-d", "heartbeat", "--no-psqlrc", "--tuples-only", "--no-align", "--set", "ON_ERROR_STOP=1", "--command", query], variables, cancellationToken);
        if (result.ExitCode != 0) throw new InvalidOperationException("Could not read isolated PostgreSQL evidence.");
        return result.StdOut;
    }

    private async Task ComposeAsync(string stage, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        Evidence.Commands.Add($"docker compose --project-name {ProjectName} {string.Join(' ', arguments)}");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        ProcessResult result;
        try
        {
            result = await runner.CaptureAsync("docker", [.. compose, .. arguments], variables, timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Isolated environment {stage} exceeded its command timeout.");
        }
        await File.WriteAllTextAsync(Path.Combine(Evidence.Run.Directory, $"{stage}.log"), result.StdOut + result.StdErr, CancellationToken.None);
        if (result.ExitCode != 0) throw new InvalidOperationException($"Isolated environment {stage} failed; see {stage}.log.");
    }

    private async Task<bool> RemoveAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await ComposeAsync("cleanup", ["down", "--volumes", "--remove-orphans"], timeout.Token);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
