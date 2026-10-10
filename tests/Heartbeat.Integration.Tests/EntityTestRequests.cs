using System.Net.Http.Json;
using System.Text.Json;
using Heartbeat.Core;

namespace Heartbeat.Integration.Tests;

internal static class EntityTestRequests
{
    internal static object Business(object properties, object? references = null) =>
        new { references = references ?? new { }, properties };

    internal static JsonElement Fields { get; } = JsonSerializer.Deserialize<JsonElement>("""
        {
          "title": {"type":"string","required":false,"nullable":true},
          "count": {"type":"integer","required":false,"nullable":false},
          "amount": {"type":"number","required":false,"nullable":true},
          "enabled": {"type":"boolean","required":false,"nullable":false},
          "relatedId": {"type":"reference","required":false,"nullable":true},
          "metadata": {"type":"object","required":false,"nullable":true,"fields":{
            "visible":{"type":"boolean","required":true,"nullable":false},
            "note":{"type":"string","required":false,"nullable":true}
          }},
          "labels": {"type":"array","required":false,"nullable":true,"items":{"type":"string","nullable":true}}
        }
        """);

    internal static async Task<Guid> RegisterAsync(HttpClient client, string resourceName,
        CancellationToken cancellationToken, JsonElement? fields = null, Guid? schemaId = null)
    {
        var id = schemaId ?? EntityId.New().Value;
        using var response = await client.PutAsJsonAsync($"entities/schemas/{id}",
            Business(new { name = "Test records", resourceName, fields = fields ?? Fields }), cancellationToken);
        response.EnsureSuccessStatusCode();
        return id;
    }
}
