using System.Text;
using System.Text.Json;

namespace Orisumi.Dev;

internal sealed class ScenarioHttp(TextWriter log) : IDisposable
{
    private readonly HttpClient client = new() { Timeout = TimeSpan.FromSeconds(60) };

    public async Task<JsonElement?> RequestAsync(string method, string url, int expected, CancellationToken token,
        string? body = null, bool problem = false)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), url);
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        await log.WriteLineAsync($"> {method} {url}");
        if (body is not null) await log.WriteLineAsync(body.Length > 2048 ? $"[body: {Encoding.UTF8.GetByteCount(body)} bytes]" : body);
        using var response = await client.SendAsync(request, token);
        var text = await response.Content.ReadAsStringAsync(token);
        await log.WriteLineAsync($"< {(int)response.StatusCode} {response.Content.Headers}\n{text}");
        Require((int)response.StatusCode == expected, $"{method} {url}: expected {expected}, got {(int)response.StatusCode}.");
        if (problem) Require(response.Content.Headers.ContentType?.MediaType == "application/problem+json", "Expected application/problem+json.");
        return response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.Ordinal) == true
            ? JsonSerializer.Deserialize<JsonElement>(text) : null;
    }
    public static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    public void Dispose() => client.Dispose();
}

internal sealed record ScenarioEntity(Guid EntityId, Guid VersionId, string Body)
{
    public static ScenarioEntity Create()
    {
        var entity = Guid.CreateVersion7();
        var version = Guid.CreateVersion7();
        return new(entity, version, JsonSerializer.Serialize(new { versions = new[] { new {
            entityId = entity, versionId = version, previousVersionId = (Guid?)null, schemaVersionId = (Guid?)null,
            category = "entity_schema", content = new { displayName = "Smoke", description = "Smoke scenario",
                resourceName = "smoke-" + Guid.NewGuid().ToString("N"), fields = new { } }
        } } }));
    }
    public async Task SubmitAsync(ScenarioHttp http, string api, CancellationToken token)
    {
        var saved = await http.RequestAsync("POST", api + "/versions", 200, token, Body);
        ScenarioHttp.Require(saved!.Value.GetProperty("versions")[0].GetProperty("versionId").GetGuid() == VersionId, "Submission returned another version.");
    }
    public async Task ReadAsync(ScenarioHttp http, string api, CancellationToken token)
    {
        var read = await http.RequestAsync("GET", api + $"/versions/{VersionId}", 200, token);
        ScenarioHttp.Require(read!.Value.GetProperty("entityId").GetGuid() == EntityId, "Read returned another entity.");
        ScenarioHttp.Require(read.Value.GetProperty("versionId").GetGuid() == VersionId, "Read returned another version.");
        using var submitted = JsonDocument.Parse(Body);
        ScenarioHttp.Require(JsonElement.DeepEquals(read.Value.GetProperty("content"),
            submitted.RootElement.GetProperty("versions")[0].GetProperty("content")), "Stored content changed.");
    }
}
