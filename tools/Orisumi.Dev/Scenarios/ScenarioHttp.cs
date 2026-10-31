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

internal sealed class ScenarioEntity
{
    public Guid EntityId { get; private set; }
    public Guid VersionId { get; private set; }
    public object Observer { get; } = new { schemaId = Guid.CreateVersion7(), key = new { scope = (string?)null, scheme = "smoke", value = Guid.NewGuid().ToString() } };
    public static ScenarioEntity Create() => new();
    public async Task SubmitAsync(ScenarioHttp http, string api, CancellationToken token)
    {
        var reference = JsonSerializer.SerializeToElement(Observer);
        var schemaId = reference.GetProperty("schemaId").GetGuid();
        var body = JsonSerializer.Serialize(new { observer = Observer,
            schemas = new[] { new { schemaId, content = new { displayName = "Smoke", description = "Smoke scenario", resourceName = "smoke-" + Guid.NewGuid().ToString("N"), fields = new { } } } },
            writes = new[] { new { schemaId, key = reference.GetProperty("key"), content = new { } } } });
        var saved = await http.RequestAsync("POST", api + "/writes", 200, token, body);
        var version = saved!.Value.GetProperty("results")[0].GetProperty("version");
        EntityId = version.GetProperty("entityId").GetGuid();
        VersionId = version.GetProperty("versionId").GetGuid();
    }
    public async Task ReadAsync(ScenarioHttp http, string api, CancellationToken token)
    {
        var read = await http.RequestAsync("GET", api + $"/versions/{VersionId}", 200, token);
        ScenarioHttp.Require(read!.Value.GetProperty("entityId").GetGuid() == EntityId, "Read returned another entity.");
        ScenarioHttp.Require(read.Value.GetProperty("versionId").GetGuid() == VersionId, "Read returned another version.");
        ScenarioHttp.Require(!read.Value.GetProperty("content").EnumerateObject().Any(), "Stored content changed.");
    }
}
