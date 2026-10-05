using System.Text.Json;

namespace Heartbeat.Recording;

internal static class JsonValue
{
    public static JsonElement Clone(JsonElement value, string parameterName)
    {
        if (value.ValueKind is JsonValueKind.Undefined)
        {
            throw new ArgumentException("A JSON value is required.", parameterName);
        }

        return value.Clone();
    }
}
