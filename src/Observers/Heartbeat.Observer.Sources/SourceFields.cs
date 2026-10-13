using System.Text.Json;

namespace Heartbeat.Observers.Sources;

public static class SourceFields
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720", Justification = "Factory names mirror the schema contract types.")]
    public static object String(bool nullable = false, bool required = true) => new { type = "string", required, nullable };
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720", Justification = "Factory names mirror the schema contract types.")]
    public static object Integer(bool nullable = false, bool required = true) => new { type = "integer", required, nullable };
    public static object Number(bool nullable = false, bool required = true) => new { type = "number", required, nullable };
    public static object Boolean(bool nullable = false, bool required = true) => new { type = "boolean", required, nullable };
    public static object Reference(bool nullable = false, bool required = true) => new { type = "reference", required, nullable };
    public static object Array(object items, bool nullable = false, bool required = true) => new { type = "array", required, nullable, items };
    public static JsonElement Of(object value) => JsonSerializer.SerializeToElement(value);

    public static JsonElement WithIdentity(object fields)
    {
        var values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(fields))!;
        values.Add("sourceEntityId", Of(Reference()));
        return Of(values);
    }
}
