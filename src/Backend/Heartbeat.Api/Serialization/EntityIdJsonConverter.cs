using System.Text.Json;
using System.Text.Json.Serialization;
using Heartbeat.Core;

namespace Heartbeat.Api.Serialization;

public sealed class EntityIdJsonConverter : JsonConverter<EntityId>
{
    public override EntityId Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("EntityId must be a UUIDv7 string.");
        }

        var text = reader.GetString();
        if (text is not { Length: 36 }
            || !Guid.TryParseExact(text, "D", out var id)
            || id.Version != 7
            || text[19] is not ('8' or '9' or 'a' or 'A' or 'b' or 'B'))
        {
            throw new JsonException("EntityId must be a UUIDv7 string.");
        }

        return new EntityId(id);
    }

    public override void Write(
        Utf8JsonWriter writer,
        EntityId value,
        JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.Value);
    }
}
