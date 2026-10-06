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

        if (!EntityIdParser.TryParse(reader.GetString(), out var id))
        {
            throw new JsonException("EntityId must be a UUIDv7 string.");
        }

        return id;
    }

    public override void Write(
        Utf8JsonWriter writer,
        EntityId value,
        JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.Value);
    }
}
