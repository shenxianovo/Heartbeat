using System.Net.Http.Json;
using System.Text.Json;
using Orisumi.Core;

namespace Orisumi.Testing;

/// <summary>Creates test data through the public write contract.</summary>
public sealed class WriteClient(HttpClient http)
{
    private static readonly Guid ObserverSchema = Guid.Parse("019e1bbb-ec00-7000-8000-00000000ffff");
    private readonly string _observerKey = Guid.CreateVersion7().ToString();
    public EntityVersion Observer { get; private set; } = null!;
    public EntityId ObserverId => ((Observation)Observer.Content).ObserverId;
    public object ObserverReference => new { schemaId = Guid.Parse("019e1bbb-ec00-7000-8000-000000000012"), key = new { scope = (string?)null, scheme = "test", value = _observerKey } };

    public async Task InitializeAsync()
    {
        await SendAsync([], [new { schemaId = ObserverSchema, content = new { displayName = "Test Observer", description = "Test description", resourceName = "test-observers", kind = "observation", immutable = false,
            fields = new { testKey = new { type = "string", description = "Test instance", required = true, nullable = false } } } }],
            description: new { schemaId = ObserverSchema, content = new { testKey = _observerKey } });
        using var response = await http.GetAsync("observations?resourceName=test-observers&limit=1000");
        var page = await JsonAsync(response);
        Observer = page.GetProperty("versions").EnumerateArray().Select(VersionJson.ReadSaved)
            .Single(v => ((Observation)v.Content).Fields.GetProperty("testKey").GetString() == _observerKey);
    }

    public async Task<EntityId> DeclareAsync(string resource = "items", string fields = "{}", EntityId? schemaId = null, string kind = "observation", bool immutable = false)
    {
        var id = schemaId ?? EntityId.New();
        await SendAsync([], [new { schemaId = id.Value, content = new { displayName = resource, description = "One test item.", resourceName = resource, kind, immutable, fields = JsonSerializer.Deserialize<JsonElement>(fields) } }]);
        return id;
    }
    public async Task<JsonElement> SendAsync(object[] writes, object[]? schemas = null, long? sequence = null, object? description = null)
    {
        var body = new Dictionary<string, object?> { ["observer"] = ObserverReference, ["writes"] = writes };
        if (description is not null) body["description"] = description;
        if (schemas is not null) body["schemas"] = schemas;
        if (sequence is not null) body["sequence"] = sequence;
        using var response = await http.PostAsJsonAsync("/writes", body);
        var json = await JsonAsync(response);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException(json.GetRawText(), null, response.StatusCode);
        return json;
    }
    public async Task<EntityVersion> WriteAsync(EntityId schema, object content, string? key = null,
        object? time = null, VersionId[]? inputs = null)
    {
        var json = await SendAsync([Write(schema, content, key, time, inputs)]);
        var result = json.GetProperty("results")[0];
        if (result.TryGetProperty("error", out var error)) throw new InvalidOperationException(error.GetRawText());
        return VersionJson.ReadSaved(result.GetProperty("version"));
    }
    public static object Write(EntityId schema, object content, string? key = null,
        object? time = null, VersionId[]? inputs = null)
    {
        var result = new Dictionary<string, object?> { ["schemaId"] = schema.Value, ["content"] = content, ["formation"] = "direct" };
        if (key is not null) result["key"] = new { scope = (string?)null, scheme = "test", value = key };
        if (time is not null) result["time"] = time;
        if (inputs is not null) result["inputs"] = inputs.Select(id => id.Value).ToArray();
        return result;
    }
    public static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
}
