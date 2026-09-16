using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Heartbeat.Dev;

internal sealed class NativeDesktopScenario(
    RepositoryContext repository,
    IProcessRunner runner,
    TextWriter output)
{
    public async Task<int> RunAsync(ScenarioOptions options, CancellationToken cancellationToken)
    {
        ValidatePlatform();
        var dotenv = ReadConfiguration();
        var run = new ArtifactStore(repository).Create("scenario", "native-desktop");
        var started = DateTimeOffset.UtcNow;
        var port = TcpPort.Reserve();
        var projectName = $"heartbeat-verify-{run.Id[^8..]}".ToLowerInvariant();
        var compose = ComposeInvocation.Create(
            repository, repository.Path(".env.local"), release: false, projectName);
        var composeEnvironment = new Dictionary<string, string?>
        {
            ["HEARTBEAT_HUB_PORT"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        var commands = new List<string>();
        var artifacts = new List<string>();
        Process? collector = null;
        var exitCode = 1;
        try
        {
            commands.Add($"docker compose --project-name {projectName} up --build --detach hub");
            var up = await runner.CaptureAsync("docker", [.. compose, "up", "--build", "--detach", "hub"], composeEnvironment, cancellationToken);
            if (up.ExitCode != 0) throw new InvalidOperationException(up.StdErr + up.StdOut);

            var hub = new Uri($"http://127.0.0.1:{port}/");
            await WaitForHubAsync(hub, cancellationToken);
            var before = await ReadStatusAsync(hub, dotenv.Get("HEARTBEAT_HUB_TOKEN")!, cancellationToken);
            var nativeStarted = DateTimeOffset.UtcNow;
            var collectorAssembly = await BuildCollectorAsync(run, commands, artifacts, cancellationToken);
            commands.Add("dotnet src/Collectors/Heartbeat.Collector.Desktop.Mac/bin/Debug/net10.0/Heartbeat.Collector.Desktop.Mac.dll");
            collector = StartCollector(collectorAssembly, hub, dotenv);
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
            if (collector.HasExited)
            {
                throw new InvalidOperationException($"Collector exited before the guided session: {await ReadCollectorOutputAsync(collector)}");
            }

            await output.WriteLineAsync("""
                Native Collector is running in an isolated verification environment.
                Perform a short, non-sensitive sequence now: switch applications, type a few disposable characters,
                and use the mouse. Return here and press Enter to finish evidence collection.
                """);
            await Console.In.ReadLineAsync(cancellationToken);
            await ProcessRunner.InterruptAsync(collector, TimeSpan.FromSeconds(15), cancellationToken);
            var nativeCompleted = DateTimeOffset.UtcNow;
            var after = await ReadStatusAsync(hub, dotenv.Get("HEARTBEAT_HUB_TOKEN")!, cancellationToken);
            if (!HasNewQueueEvidence(before, after))
            {
                throw new InvalidOperationException(
                    "The Collector exited cleanly, but the Hub queue did not receive a new record. Repeat the guided interaction and check macOS permissions.");
            }
            var collectorOutput = await ReadCollectorOutputAsync(collector);
            await WriteMetadataAsync(run, nativeStarted, nativeCompleted, collector.ExitCode, before, after, cancellationToken);
            artifacts.Add("native-session.json");
            if (options.IncludeSensitiveEvidence)
            {
                await File.WriteAllTextAsync(Path.Combine(run.Directory, "collector.log"), collectorOutput, cancellationToken);
                artifacts.Add("collector.log");
            }
            exitCode = collector.ExitCode;
        }
        catch (OperationCanceledException)
        {
            exitCode = 130;
            throw;
        }
        finally
        {
            var keep = exitCode != 0 && options.KeepEnvironmentOnFailure;
            var removed = false;
            try
            {
                if (collector is { HasExited: false })
                {
                    collector.Kill(entireProcessTree: true);
                    await collector.WaitForExitAsync(CancellationToken.None);
                }
                if (!keep)
                {
                    commands.Add($"docker compose --project-name {projectName} down --volumes --remove-orphans");
                    var cleanup = await runner.CaptureAsync("docker", [.. compose, "down", "--volumes", "--remove-orphans"], composeEnvironment, CancellationToken.None);
                    removed = cleanup.ExitCode == 0;
                    if (!removed && exitCode == 0) exitCode = 1;
                }
            }
            catch (Exception exception)
            {
                if (exitCode == 0) exitCode = 1;
                await output.WriteLineAsync($"Cleanup of {projectName} failed ({exception.GetType().Name}).");
            }
            finally
            {
                await ArtifactStore.WriteManifestAsync(run, new EvidenceManifest(
                    run.Id, "scenario", "native-desktop", started, DateTimeOffset.UtcNow, exitCode,
                    options.IncludeSensitiveEvidence, commands, artifacts,
                    [
                        "Human interaction is required because macOS input monitoring and accessibility actions are intentionally not automated.",
                    options.IncludeSensitiveEvidence
                        ? "Collector logs were explicitly retained and may contain user context."
                        : "Only timing, process, and Hub queue metadata were retained; no screen or input content was saved.",
                    keep ? $"The failed environment was retained as Docker Compose project {projectName}."
                        : removed ? "The isolated Docker environment was removed."
                        : $"Cleanup was not confirmed for Docker Compose project {projectName}.",
                    ]), CancellationToken.None);
                collector?.Dispose();
                await output.WriteLineAsync($"Scenario evidence: {run.Directory}");
            }
        }
        return exitCode;
    }

    private static void ValidatePlatform()
    {
        if (!OperatingSystem.IsMacOS())
            throw new InvalidOperationException("native-desktop currently requires macOS; the CLI surface remains cross-platform.");
    }

    private DotenvFile ReadConfiguration()
    {
        var path = repository.Path(".env.local");
        if (!File.Exists(path)) throw new FileNotFoundException("Run ./scripts/setup.sh before native verification.", path);
        var dotenv = DotenvFile.Read(path);
        var required = new[] { "HEARTBEAT_API_KEY", "HEARTBEAT_OWNER_ID", "HEARTBEAT_HUB_TOKEN", "HEARTBEAT_COLLECTOR_TARGET" };
        var missing = required.Where(name => string.IsNullOrWhiteSpace(dotenv.Get(name))).ToArray();
        if (missing.Length > 0) throw new InvalidOperationException($"Missing native verification configuration: {string.Join(", ", missing)}.");
        return dotenv;
    }

    private static async Task WaitForHubAsync(Uri hub, CancellationToken cancellationToken)
    {
        using var client = new HttpClient { BaseAddress = hub, Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTimeOffset.UtcNow.AddMinutes(3);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                using var response = await client.GetAsync("hub/v1/status", cancellationToken);
                if (response.StatusCode == HttpStatusCode.Unauthorized) return;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
        throw new TimeoutException($"Isolated Hub at {hub} did not become ready.");
    }

    private static async Task<string> ReadStatusAsync(Uri hub, string token, CancellationToken cancellationToken)
    {
        using var client = new HttpClient { BaseAddress = hub };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.GetStringAsync("hub/v1/status", cancellationToken);
    }

    internal static bool HasNewQueueEvidence(string before, string after)
    {
        using var beforeJson = JsonDocument.Parse(before);
        using var afterJson = JsonDocument.Parse(after);
        return QueueSize(afterJson.RootElement) > QueueSize(beforeJson.RootElement);
    }

    private static int QueueSize(JsonElement status) =>
        status.GetProperty("pending").GetInt32() + status.GetProperty("failed").GetInt32();

    private async Task<string> BuildCollectorAsync(
        ArtifactRun run,
        List<string> commands,
        List<string> artifacts,
        CancellationToken cancellationToken)
    {
        try
        {
            return await CollectorBuild.EnsureAsync(repository, runner, run, commands, cancellationToken);
        }
        finally
        {
            artifacts.Add(CollectorBuild.LogName);
        }
    }

    private Process StartCollector(string assembly, Uri hub, DotenvFile dotenv)
    {
        var environment = CollectorEnvironment.Create(hub, dotenv);
        return ProcessRunner.StartManaged(repository.Root, assembly, [], environment);
    }

    private static async Task<string> ReadCollectorOutputAsync(Process process) =>
        await process.StandardOutput.ReadToEndAsync() + await process.StandardError.ReadToEndAsync();

    private static async Task WriteMetadataAsync(
        ArtifactRun run,
        DateTimeOffset started,
        DateTimeOffset completed,
        int collectorExitCode,
        string before,
        string after,
        CancellationToken cancellationToken)
    {
        using var beforeJson = JsonDocument.Parse(before);
        using var afterJson = JsonDocument.Parse(after);
        await File.WriteAllTextAsync(Path.Combine(run.Directory, "native-session.json"), JsonSerializer.Serialize(new
        {
            nativeStarted = started,
            nativeCompleted = completed,
            durationSeconds = (completed - started).TotalSeconds,
            collectorExitCode,
            hubStatusBefore = beforeJson.RootElement,
            hubStatusAfter = afterJson.RootElement,
        }, JsonOptions.Indented) + Environment.NewLine, cancellationToken);
    }
}
