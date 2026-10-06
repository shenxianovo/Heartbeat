using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Heartbeat.Api.Serialization;

public sealed partial class UtcDateTimeOffsetJsonConverter : JsonConverter<DateTimeOffset>
{
    internal const string InputPattern = "^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\\.[0-9]+)?(?:Z|[+-][0-9]{2}:[0-9]{2})$";

    [GeneratedRegex(InputPattern, RegexOptions.CultureInvariant)]
    private static partial Regex TimestampPattern();

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
        if (text is null || !TimestampPattern().IsMatch(text)
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
