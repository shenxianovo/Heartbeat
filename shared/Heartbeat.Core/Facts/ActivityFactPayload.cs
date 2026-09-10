using System.Text.Json;
using System.Text.Json.Nodes;

namespace Heartbeat.Core.Facts;

/// <summary>Pre-Target first-party activity payload spelling; retire with the task 05 replay gate.</summary>
public static class ActivityFactPayload
{
    public static JsonElement Normalize(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("identityKey", out var legacy) ||
            legacy.ValueKind != JsonValueKind.String) return payload;
        var node = JsonNode.Parse(payload.GetRawText())!.AsObject();
        if (node.TryGetPropertyValue("activityKey", out var current) &&
            (current is not JsonValue value || !value.TryGetValue<string>(out var text) || text != legacy.GetString()))
            throw new ArgumentException("Activity identityKey conflicts with activityKey.");
        node.Remove("identityKey");
        node["activityKey"] = legacy.GetString();
        return JsonSerializer.SerializeToElement(node);
    }
}
