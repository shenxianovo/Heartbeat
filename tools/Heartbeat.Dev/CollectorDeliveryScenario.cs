using System.Text.Json;

namespace Heartbeat.Dev;

internal sealed class CollectorDeliveryScenario(RepositoryContext repository, IProcessRunner runner, TextWriter output)
{
    public Task<int> RunAsync(ScenarioOptions options, CancellationToken cancellationToken) =>
        ScenarioEnvironment.RunAsync(repository, runner, output, options,
        [
            "Real macOS Collector --once, Hub HTTP/SQLite, API authentication and PostgreSQL; requires existing Auth credentials and network access.",
            "A native foreground snapshot does not verify notifications, sustained collection, physical input, permission changes, lock/sleep or Web replay.",
            "Checks native application payload structure and collection time, not the exact identity of an independently controlled foreground application.",
            options.IncludeSensitiveEvidence ? "Collector logs were explicitly retained and may contain user context."
                : "Only counts, timing and validation results are retained; native payloads and service logs are not exported.",
        ], environment => RunChainAsync(environment, options, cancellationToken), cancellationToken);

    private async Task<int> RunChainAsync(ScenarioEnvironment environment, ScenarioOptions options, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsMacOS()) throw new InvalidOperationException("collector-delivery requires a logged-in macOS desktop.");
        // Given an empty database and a real Hub, with API delivery deliberately unavailable.
        await output.WriteLineAsync("Starting isolated PostgreSQL and Hub; initializing the database.");
        await environment.StartAsync(cancellationToken, "db", "hub");
        await environment.InitializeDatabaseAsync(cancellationToken);
        using var hub = environment.ConnectHub();
        await HubQueueStatus.WaitReadyAsync(hub, cancellationToken);
        var target = Guid.NewGuid();
        (await ReadDatabaseAsync(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, cancellationToken)).RequireEmpty();

        // When the production executable captures native observations and submits its own Records.
        await output.WriteLineAsync("Starting the real Collector for one native snapshot.");
        await using var collector = await ScenarioCollector.StartAsync(repository, runner, environment.Evidence,
            environment.Hub, environment.Token, $"verification-{target:N}", once: true,
            options.IncludeSensitiveEvidence, cancellationToken);
        var snapshot = await collector.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        if (snapshot.ExitCode != 0) throw new InvalidOperationException($"Collector exited with code {snapshot.ExitCode}; Hub custody was not confirmed.");
        var accepted = await HubQueueStatus.ReadAsync(hub, cancellationToken);
        await environment.WriteAsync("custody.json", new { snapshot.Started, snapshot.Completed, snapshot.ExitCode, queue = accepted }, cancellationToken);
        accepted.RequireAccepted();

        // Then the Hub registers identities, uploads every accepted Record and clears its queue.
        await output.WriteLineAsync($"Hub accepted {accepted.Pending} Records. Starting API and waiting for database delivery.");
        await environment.StartAsync(cancellationToken, "api");
        await ScenarioWait.UntilAsync("Record delivery and an empty Hub queue", async token =>
        {
            var queue = await HubQueueStatus.ReadAsync(hub, token);
            var database = await ReadDatabaseAsync(snapshot.Started, snapshot.Completed, token);
            await environment.WriteAsync("delivery.json", new { queue, database }, token);
            if (!queue.IsDrained()) return false;
            database.RequireDelivered(accepted.Pending);
            return true;
        }, cancellationToken);
        await output.WriteLineAsync("Verified Collector registration, native Record storage and an empty Hub queue.");
        return 0;

        async Task<CollectorDeliveryEvidence> ReadDatabaseAsync(DateTimeOffset started, DateTimeOffset completed, CancellationToken token)
        {
            var query = CollectorDeliveryEvidence.Query(environment.Owner, target, started, completed);
            return JsonSerializer.Deserialize<CollectorDeliveryEvidence>(await environment.QueryDatabaseAsync(query, token))
                ?? throw new InvalidDataException("Missing PostgreSQL delivery evidence.");
        }
    }
}
