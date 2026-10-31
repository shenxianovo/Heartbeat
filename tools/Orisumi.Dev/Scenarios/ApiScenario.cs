using System.Text.Json;

namespace Orisumi.Dev;

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
        var body = JsonSerializer.Serialize(new { observer = entity.Observer, writes = new[] { new { schemaId = Guid.CreateVersion7(), content = new { } } } });
        var result = await http.RequestAsync("POST", api + "/writes", 200, token, body);
        ScenarioHttp.Require(result!.Value.GetProperty("results")[0].GetProperty("error").GetProperty("rule").GetString() == "SCHEMA-3", "Undeclared type must be rejected by SCHEMA-3.");
    }
}
