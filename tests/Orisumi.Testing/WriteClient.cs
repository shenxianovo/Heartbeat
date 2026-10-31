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
    public object ObserverReference => new { schemaId = ObserverSchema, key = new { scope = (string?)null, scheme = "test", value = _observerKey } };

    public async Task InitializeAsync()
    {
        var definition = new EntitySchema("Test Observer", "Observer responsible for test data.", "test-observers", JsonSerializer.SerializeToElement(new { }));
        var response = await SendAsync([new { schemaId = ObserverSchema, key = new { scope = (string?)null, scheme = "test", value = _observerKey }, content = new { } }],
            [new { schemaId = ObserverSchema, content = VersionJson.Content(definition) }]);
        Observer = VersionJson.ReadSaved(response.GetProperty("results")[0].GetProperty("version"));
    }

    public async Task<EntityId> DeclareAsync(string resource = "items", string fields = "{}", EntityId? schemaId = null)
    {
        var id = schemaId ?? EntityId.New();
        await SendAsync([], [new { schemaId = id.Value, content = new { displayName = resource, description = "One test item.", resourceName = resource, fields = JsonSerializer.Deserialize<JsonElement>(fields) } }]);
        return id;
    }
    public async Task<JsonElement> SendAsync(object[] writes, object[]? schemas = null, long? sequence = null)
    {
        var body = new Dictionary<string, object?> { ["observer"] = ObserverReference, ["writes"] = writes };
        if (schemas is not null) body["schemas"] = schemas;
        if (sequence is not null) body["sequence"] = sequence;
        using var response = await http.PostAsJsonAsync("/writes", body);
        var json = await JsonAsync(response);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException(json.GetRawText(), null, response.StatusCode);
        return json;
    }
    public async Task<EntityVersion> WriteAsync(EntityId schema, object content, string? key = null, EntityId? entityId = null,
        object? time = null, VersionId[]? inputs = null)
    {
        var json = await SendAsync([Write(schema, content, key, entityId, time, inputs)]);
        var result = json.GetProperty("results")[0];
        if (result.TryGetProperty("error", out var error)) throw new InvalidOperationException(error.GetRawText());
        return VersionJson.ReadSaved(result.GetProperty("version"));
    }
    public static object Write(EntityId schema, object content, string? key = null, EntityId? entityId = null,
        object? time = null, VersionId[]? inputs = null)
    {
        var result = new Dictionary<string, object?> { ["schemaId"] = schema.Value, ["content"] = content };
        if (key is not null) result["key"] = new { scope = (string?)null, scheme = "test", value = key };
        if (entityId is not null) result["entityId"] = entityId.Value.Value;
        if (time is not null) result["time"] = time;
        if (inputs is not null) result["inputs"] = inputs.Select(id => id.Value).ToArray();
        return result;
    }
    public static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
}
