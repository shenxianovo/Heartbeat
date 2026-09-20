using System.Text.Json;

namespace Heartbeat.Dev;

internal sealed class CollectorReplayScenario(RepositoryContext repository, IProcessRunner runner, TextWriter output)
{
    public Task<int> RunAsync(ScenarioOptions options, CancellationToken cancellationToken) =>
        ScenarioEnvironment.RunAsync(repository, runner, output, options,
        [
            "Real native application, continuous Collector, Hub registration/delivery, PostgreSQL, production Web and interactive OIDC login.",
            "Starts the existing executable; client installation is not implemented or verified. Requires signing in as the configured Hub Owner.",
            "Only the temporary Chromium maps localhost:3000 to the isolated Web port; existing local services are untouched.",
            "No browser tokens, login screenshots or network traces are retained. Default screenshots contain only the controlled application's selected Record.",
            "Does not verify window/input permissions, lock/sleep, long-running stability or installation.",
        ], environment => RunChainAsync(environment, options, cancellationToken), cancellationToken);

    private async Task<int> RunChainAsync(ScenarioEnvironment environment, ScenarioOptions options, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsMacOS()) throw new InvalidOperationException("collector-replay requires a logged-in macOS desktop.");
        await StageAsync("environment");
        await environment.StartAsync(cancellationToken, "web", "hub");
        using var hub = environment.ConnectHub();
        await HubQueueStatus.WaitReadyAsync(hub, cancellationToken);
        var target = Guid.NewGuid();
        var empty = await environment.QueryDatabaseAsync(
            CollectorDeliveryEvidence.Query(environment.Owner, target, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow), cancellationToken);
        JsonSerializer.Deserialize<CollectorDeliveryEvidence>(empty)!.RequireEmpty();

        await StageAsync("native-collection");
        await output.WriteLineAsync("Activating a temporary Heartbeat Replay Probe window. Keep it foreground until collection completes.");
        CollectorReplayEvidence? witness = null;
        DateTimeOffset started;
        await using (var application = await ReplayApplication.StartAsync(repository, runner, environment.Evidence, cancellationToken))
        {
            started = DateTimeOffset.UtcNow;
            await using var collector = await ScenarioCollector.StartAsync(repository, runner, environment.Evidence,
                environment.Hub, environment.Token, $"verification-{target:N}", once: false,
                options.IncludeSensitiveEvidence, cancellationToken);
            try
            {
                await ScenarioWait.UntilAsync("the controlled application's confirmed interval in PostgreSQL", async token =>
                {
                    if (collector.Completion.IsCompleted) throw new InvalidOperationException("Collector exited before the controlled Record was delivered.");
                    var progress = await environment.QueryDatabaseAsync(CollectorReplayEvidence.ProgressQuery(environment.Owner, target), token, "collection-progress.sql");
                    await environment.WriteAsync("collection.json", new
                    {
                        queue = await HubQueueStatus.ReadAsync(hub, token),
                        database = JsonSerializer.Deserialize<JsonElement>(progress),
                    }, token);
                    witness = await ReadWitnessAsync(token);
                    return witness is not null;
                }, cancellationToken);
                var stopped = await collector.StopAsync(cancellationToken);
                if (stopped.ExitCode is not (0 or 130)) throw new InvalidOperationException("Collector did not stop cleanly.");
            }
            finally
            {
                await application.StopAsync(CancellationToken.None);
            }
        }
        await StageAsync("delivery");
        await ScenarioWait.UntilAsync("the stopped Collector's Hub queue to drain", async token =>
        {
            var queue = await HubQueueStatus.ReadAsync(hub, token);
            await environment.WriteAsync("custody.json", queue, token);
            return queue.IsDrained();
        }, cancellationToken);
        var delivered = await ReadWitnessAsync(cancellationToken)
            ?? throw new InvalidOperationException("The delivered application Record disappeared.");
        delivered.RequireContinuationOf(witness!);
        await environment.WriteAsync("replay-expectation.json", new
        {
            record = delivered,
            target = $"verification-{target:N}",
            applicationId = ReplayApplication.Identity,
            applicationName = ReplayApplication.DisplayName,
        }, cancellationToken);

        await StageAsync("browser");
        await output.WriteLineAsync("Opening isolated Chromium. Sign in with the same Heartbeat account configured for the Hub; login has a five-minute deadline.");
        var workspace = await WebVerificationWorkspace.PrepareAsync(repository, environment.Evidence.Run, cancellationToken);
        var arguments = new[] { Path.Combine(workspace, "tests", "scenarios", "collector-replay.ts"),
            environment.Evidence.Run.Directory, environment.Web.Port.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        environment.Evidence.Commands.Add("node tests/scenarios/collector-replay.ts (isolated Web, real OIDC)");
        await using var browser = new ScenarioProcess(workspace, "node", arguments,
            logPath: Path.Combine(environment.Evidence.Run.Directory, "browser.log"));
        var result = await browser.WaitAsync(TimeSpan.FromMinutes(7), cancellationToken);
        if (result.ExitCode != 0) throw new InvalidOperationException("Browser replay failed; see replay.json for the failing stage.");
        await StageAsync("completed");
        return 0;

        async Task StageAsync(string stage)
        {
            await output.WriteLineAsync($"Collector replay: {stage}");
            await environment.WriteAsync("stage.json", new { stage }, cancellationToken);
        }

        async Task<CollectorReplayEvidence?> ReadWitnessAsync(CancellationToken token)
        {
            var json = await environment.QueryDatabaseAsync(CollectorReplayEvidence.Query(environment.Owner, target, started, DateTimeOffset.UtcNow), token);
            return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<CollectorReplayEvidence>(json);
        }
    }
}
