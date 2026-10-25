using System.Text.Json;

namespace Heartbeat.Dev;

internal sealed class EnvironmentScenario(RepositoryContext repository, IProcessRunner runner) : ISmokeScenario
{
    public string Name => "environment";
    public async Task<ScenarioPrerequisite> CheckAsync(CancellationToken token) =>
        (await new EnvironmentManager(repository, runner, TextWriter.Null).StatusAsync(token)).Running
            ? new(false, "This checkout already has a running environment.")
            : new(true, "No environment is running; the scenario will manage a temporary volume.");

    public async Task ExecuteAsync(TextWriter log, CancellationToken token)
    {
        var manager = new EnvironmentManager(repository, runner, log);
        using var http = new ScenarioHttp(log);
        var volume = EnvironmentFiles.TemporaryVolume();
        try
        {
            var state = await manager.UpAsync([], true, token, volume);
            await log.WriteLineAsync(JsonSerializer.Serialize(state, JsonOptions.Indented));
            CheckAssignedPorts(new EnvironmentFiles(repository).Read()!.Ports, state);
            var entries = state.Entries!;
            await RoutingAsync(http, entries, token);
            await http.RequestAsync("POST", entries.Api + "/versions", 413, token, new string('x', 10 * 1024 * 1024 + 1), problem: true);
            var entity = ScenarioEntity.Create();
            await entity.SubmitAsync(http, entries.Api, token);
            await manager.DownAsync(token, preserveVolume: true);

            state = await manager.UpAsync(["api"], true, token, volume);
            entries = state.Entries!;
            await entity.ReadAsync(http, entries.Api, token);
            await http.RequestAsync("GET", entries.Docs, 503, token, problem: true);
            await http.RequestAsync("GET", entries.Web, 503, token, problem: true);
            await http.RequestAsync("GET", entries.Docs + "api/entities", 200, token);
            await manager.DownAsync(token, preserveVolume: true);

            state = await manager.UpAsync(["docs"], true, token, volume);
            entries = state.Entries!;
            await http.RequestAsync("GET", entries.Docs + "core", 200, token);
            await http.RequestAsync("GET", entries.Api + "/entities", 503, token, problem: true);
            await http.RequestAsync("GET", entries.Docs + "api/entities", 503, token, problem: true);
            await http.RequestAsync("GET", entries.Web + "api/entities", 503, token, problem: true);
            await manager.DownAsync(token, preserveVolume: true);

            await VerifyMigrationFailureAsync(manager, volume, log, token);
        }
        finally
        {
            await log.WriteLineAsync("Cleanup: stop environment and remove temporary volume.");
            await manager.DownAsync(CancellationToken.None);
        }
    }

    private static async Task VerifyMigrationFailureAsync(EnvironmentManager manager, string volume, TextWriter log, CancellationToken token)
    {
        try
        {
            await manager.UpAsync(["api"], true, token, volume, failMigration: true);
        }
        catch (InvalidOperationException)
        {
            var state = await manager.StatusAsync(token);
            await log.WriteLineAsync(JsonSerializer.Serialize(state, JsonOptions.Indented));
            var migration = state.Services?.SingleOrDefault(service => service.Name == "migrate");
            ScenarioHttp.Require(migration?.ExitCode is not null and not 0, "Expected the invalid migration to finish with a nonzero exit code.");
            ScenarioHttp.Require(state.Services!.Any(service => service.Name == "api" && service.State != "Running"), "API started despite migration failure.");
            var migrationLogs = await manager.AspireAsync(["logs", "migrate"], token);
            await log.WriteLineAsync(migrationLogs.StdOut + migrationLogs.StdErr);
            return;
        }
        throw new InvalidOperationException("The deliberately invalid migration unexpectedly started API.");
    }

    private static void CheckAssignedPorts(EnvironmentPorts ports, EnvironmentStatus state)
    {
        var entries = state.Entries!;
        var connection = new System.Data.Common.DbConnectionStringBuilder { ConnectionString = state.Database!.ConnectionString };
        ScenarioHttp.Require(new Uri(entries.Api).Port == ports.Api && new Uri(entries.Docs).Port == ports.Docs
            && new Uri(entries.Web).Port == ports.Web && Convert.ToInt32(connection["Port"], System.Globalization.CultureInfo.InvariantCulture) == ports.Database,
            "Actual entry and database ports must match the saved environment ports.");
    }

    private static async Task RoutingAsync(ScenarioHttp http, EnvironmentEntries entries, CancellationToken token)
    {
        await http.RequestAsync("GET", entries.Api + "/entities", 200, token);
        await http.RequestAsync("GET", new Uri(new Uri(entries.Api), "/").ToString(), 404, token);
        await http.RequestAsync("GET", entries.Docs + "core", 200, token);
        await http.RequestAsync("GET", entries.Docs + "api", 200, token);
        await http.RequestAsync("GET", entries.Web, 200, token);
        foreach (var prefix in new[] { entries.Api + "/", entries.Docs + "api/", entries.Web + "api/" })
        {
            await http.RequestAsync("GET", prefix + "entities", 200, token);
            await http.RequestAsync("GET", prefix + "versions/019a0d3b-1d20-7000-8000-000000000001", 404, token, problem: true);
            await http.RequestAsync("GET", prefix + "observations?from=2026-01-01T00:00:00Z&to=2027-01-01T00:00:00Z", 200, token);
        }
    }
}
