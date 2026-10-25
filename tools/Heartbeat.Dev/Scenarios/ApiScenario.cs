using System.Text.Json;

namespace Heartbeat.Dev;

internal sealed class ApiScenario(EnvironmentManager manager) : ISmokeScenario
{
    public string Name => "api";
    public async Task<ScenarioPrerequisite> CheckAsync(CancellationToken token)
    {
        var state = await manager.StatusAsync(token);
        if (!state.Running) return new(false, "No environment is running in this checkout.");
        if (!state.Services!.Any(service => service.Name == "api" && service.State == "Running"))
            return new(false, "API is not running.");
        if (state.Database?.Temporary != true) return new(false, "API writes require a temporary database volume.");
        return new(true, "API is running with a temporary database volume.");
    }
    public async Task ExecuteAsync(TextWriter log, CancellationToken token)
    {
        var state = await manager.StatusAsync(token);
        var api = state.Entries!.Api;
        using var http = new ScenarioHttp(log);
        var entity = ScenarioEntity.Create();
        await entity.SubmitAsync(http, api, token);
        await entity.ReadAsync(http, api, token);
        var rejectedId = Guid.CreateVersion7();
        var body = JsonSerializer.Serialize(new { versions = new[] { new {
            entityId = Guid.CreateVersion7(), versionId = rejectedId, previousVersionId = (Guid?)null,
            schemaVersionId = (Guid?)null, category = "input_set", content = new { members = Array.Empty<Guid>() }
        } } });
        var problem = await http.RequestAsync("POST", api + "/versions", 400, token, body, problem: true);
        ScenarioHttp.Require(problem!.Value.GetProperty("rule").GetString() == "INPUTSET-2", "Empty input set must be rejected by INPUTSET-2.");
        ScenarioHttp.Require(problem.Value.GetProperty("versionId").GetGuid() == rejectedId && problem.Value.GetProperty("index").GetInt32() == 0,
            "Rejection must identify the rejected version and index.");
    }
}
