using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orisumi.Observer.Sdk;

public sealed record RecognitionKey([property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Scope, string Scheme, string Value);
[JsonConverter(typeof(RefConverter))]
public sealed record Ref(Guid? VersionId = null, Guid? EntityId = null, Guid? SchemaId = null, RecognitionKey? Key = null)
{
    public static Ref Version(Guid id) => new(VersionId: id);
    public static Ref Entity(Guid id) => new(EntityId: id);
    public static Ref Recognize(Guid schemaId, RecognitionKey key) => new(SchemaId: schemaId, Key: key);
}
internal sealed class RefConverter : JsonConverter<Ref>
{
    public override Ref Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var json = document.RootElement;
        if (json.ValueKind == JsonValueKind.String) return Ref.Version(json.GetGuid());
        if (json.TryGetProperty("entityId", out var id)) return Ref.Entity(id.GetGuid());
        return Ref.Recognize(json.GetProperty("schemaId").GetGuid(), json.GetProperty("key").Deserialize<RecognitionKey>(options)!);
    }
    public override void Write(Utf8JsonWriter writer, Ref value, JsonSerializerOptions options)
    {
        if (value.VersionId is { } version) { writer.WriteStringValue(version); return; }
        writer.WriteStartObject();
        if (value.EntityId is { } entity) writer.WriteString("entityId", entity);
        else
        {
            if (value.SchemaId is { } schema) writer.WriteString("schemaId", schema);
            writer.WritePropertyName("key"); JsonSerializer.Serialize(writer, value.Key, options);
        }
        writer.WriteEndObject();
    }
}
public sealed record FactTime([property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? StartAt, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? EndAt, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? TimeZone)
{
    public static FactTime Point(DateTimeOffset at, string? zone) => new(at, at, zone);
    public static FactTime Unknown() => new(null, null, null);
}
public sealed record WriteItem(Guid SchemaId, object Content, RecognitionKey? Key = null, Guid? EntityId = null,
    FactTime? Time = null, Guid[]? Inputs = null);
public sealed record SchemaDefinition(Guid SchemaId, JsonElement Content)
{
    public static SchemaDefinition[] Load(Assembly assembly, string resource)
    {
        using var stream = assembly.GetManifestResourceStream(resource) ?? throw new InvalidOperationException($"Missing schema definitions: {resource}");
        return JsonSerializer.Deserialize<SchemaDefinition[]>(stream, DataClient.Json)!;
    }
}
/// <summary>The running device from the Observer context, shared by every Observer that refers to it.</summary>
public sealed record DeviceContext(RecognitionKey Key, string? Name, string Platform)
{
    public static SchemaDefinition Definition { get; } = SchemaDefinition.Load(typeof(DeviceContext).Assembly, "Orisumi.Observer.Sdk.device.schema.json")[0];
    public Ref Ref => Ref.Recognize(Definition.SchemaId, Key);
    public WriteItem Write => new(Definition.SchemaId, new { name = Name, platform = Platform }, Key);
}
public sealed record WriteResult(JsonElement? Version, bool Changed, JsonElement? Observation, JsonElement? Error);
public sealed class WriteRejectedException(JsonElement error) : Exception(error.GetProperty("detail").GetString())
{
    public JsonElement Error { get; } = error;
}
