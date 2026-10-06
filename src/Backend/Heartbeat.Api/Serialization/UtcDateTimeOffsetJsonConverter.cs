using System.Text.Json;
using System.Text.Json.Serialization;

namespace Heartbeat.Api.Serialization;

public sealed class UtcDateTimeOffsetJsonConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("Time must be a date-time string with Z or an explicit UTC offset.");
        }

        var text = reader.GetString();
        if (text is not { Length: >= 20 }
            || text[10] != 'T'
            || text[16] != ':'
            || !(text.EndsWith('Z')
                || (text.Length >= 25 && text[^6] is '+' or '-' && text[^3] == ':'))
            || !reader.TryGetDateTimeOffset(out var value))
        {
            throw new JsonException("Time must be a date-time string with Z or an explicit UTC offset.");
        }

        return value;
    }

    public override void Write(
        Utf8JsonWriter writer,
        DateTimeOffset value,
        JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.UtcDateTime);
    }
}
