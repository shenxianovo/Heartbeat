using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;

namespace Heartbeat.Collection.Hub.Collectors.Protocol;

internal static class FactCanonicalization
{
    public static string? ValidateProtocolJson(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name))
                        return $"JSON object contains duplicate key '{property.Name}'.";
                    var propertyError = ValidateProtocolJson(property.Value);
                    if (propertyError is not null)
                        return propertyError;
                }
                return null;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    var itemError = ValidateProtocolJson(item);
                    if (itemError is not null)
                        return itemError;
                }
                return null;
            case JsonValueKind.Number:
                return IsSafeJsonNumber(element)
                    ? null
                    : "JSON number is non-finite or is an integer outside the safe 2^53-1 range.";
            case JsonValueKind.String:
            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
                return null;
            default:
                return "Undefined JSON is not a valid protocol value.";
        }
    }

    public static string ContentHash(FactSubmission fact)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaRevision", fact.SchemaRevision);
            writer.WriteString("recordState", fact.RecordState switch
            {
                FactRecordState.Present => "present",
                FactRecordState.Retracted => "retracted",
                _ => throw new InvalidOperationException("Unknown Fact record state cannot be canonicalized.")
            });
            writer.WritePropertyName("time");
            WriteFactTime(
                writer,
                fact.Time,
                value => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            if (fact.RecordState == FactRecordState.Present)
            {
                writer.WritePropertyName("payload");
                WriteCanonical(writer, fact.Payload);
            }
            writer.WriteEndObject();
        }
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(buffer.ToArray()));
    }

    public static string PublishRequestHash(IReadOnlyList<FactSubmission> facts)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var fact in facts)
            {
                writer.WriteStartObject();
                writer.WriteString("streamId", fact.StreamId);
                writer.WriteNumber("schemaRevision", fact.SchemaRevision);
                writer.WriteString("factId", fact.FactId);
                writer.WriteNumber("revision", fact.Revision);
                if (fact.ObservedAt is { } observedAt)
                    writer.WriteString("observedAt", observedAt.ToString("O", CultureInfo.InvariantCulture));
                else
                    writer.WriteNull("observedAt");
                writer.WriteNumber("recordState", (int)fact.RecordState);
                writer.WritePropertyName("time");
                WriteFactTime(
                    writer,
                    fact.Time,
                    value => value.ToString("O", CultureInfo.InvariantCulture));
                var hasPayload = fact.Payload.ValueKind != JsonValueKind.Undefined;
                writer.WriteBoolean("hasPayload", hasPayload);
                if (hasPayload)
                {
                    writer.WritePropertyName("payload");
                    WriteCanonical(writer, fact.Payload);
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(buffer.ToArray()));
    }

    public static long PublishLogicalMessageSize(
        Guid activationId,
        Guid messageId,
        IReadOnlyList<FactSubmission> facts)
    {
        using var counter = new CountingWriteStream();
        using (var writer = new Utf8JsonWriter(counter))
        {
            writer.WriteStartObject();
            writer.WriteString("protocol", "heartbeat.collector/1");
            writer.WriteString("type", "facts.publish");
            writer.WriteString("messageId", messageId);
            writer.WriteString("activationId", activationId);
            writer.WritePropertyName("body");
            writer.WriteStartObject();
            writer.WritePropertyName("facts");
            writer.WriteStartArray();
            foreach (var fact in facts)
            {
                writer.WriteStartObject();
                writer.WriteString("streamId", fact.StreamId);
                writer.WriteNumber("schemaRevision", fact.SchemaRevision);
                writer.WriteString("factId", fact.FactId);
                writer.WriteNumber("revision", fact.Revision);
                if (fact.ObservedAt is { } observedAt)
                    writer.WriteString("observedAt", FormatProtocolTimestamp(observedAt));
                writer.WriteString("recordState", fact.RecordState switch
                {
                    FactRecordState.Present => "present",
                    FactRecordState.Retracted => "retracted",
                    _ => throw new InvalidOperationException("Unknown Fact record state cannot be canonicalized.")
                });
                writer.WritePropertyName("time");
                WriteFactTime(writer, fact.Time, FormatProtocolTimestamp);
                if (fact.RecordState == FactRecordState.Present)
                {
                    writer.WritePropertyName("payload");
                    WriteCanonical(writer, fact.Payload);
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return counter.BytesWritten;
    }

    private static void WriteFactTime(
        Utf8JsonWriter writer,
        FactTime time,
        Func<DateTimeOffset, string> formatTimestamp)
    {
        writer.WriteStartObject();
        if (time.OccurredAt is { } occurredAt)
        {
            writer.WriteString("occurredAt", formatTimestamp(occurredAt));
        }
        else if (time.Start is { } start && time.End is { } end && time.IsFinal is { } isFinal)
        {
            writer.WriteString("start", formatTimestamp(start));
            writer.WriteString("end", formatTimestamp(end));
            writer.WriteBoolean("isFinal", isFinal);
        }
        else
        {
            throw new InvalidOperationException("Fact time cannot be canonically represented.");
        }
        writer.WriteEndObject();
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                WriteCanonicalNumber(writer, element);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new InvalidOperationException("Undefined JSON cannot be canonicalized.");
        }
    }

    private static void WriteCanonicalNumber(Utf8JsonWriter writer, JsonElement element)
    {
        var number = ParseCanonicalNumber(element);
        if (number.Digits == "0")
        {
            writer.WriteNumberValue(0);
            return;
        }

        var sign = number.IsNegative ? "-" : string.Empty;
        writer.WriteRawValue($"{sign}{number.Digits}e{number.Exponent.ToString(CultureInfo.InvariantCulture)}");
    }

    private static bool IsSafeJsonNumber(JsonElement element)
    {
        var number = ParseCanonicalNumber(element);
        if (number.Digits == "0" || number.Exponent < BigInteger.Zero)
            return true;

        var integerDigitCount = new BigInteger(number.Digits.Length) + number.Exponent;
        const string maxSafeIntegerDigits = "9007199254740991";
        if (integerDigitCount != maxSafeIntegerDigits.Length)
            return integerDigitCount < maxSafeIntegerDigits.Length;

        var integerDigits = number.Digits.PadRight((int)integerDigitCount, '0');
        return string.CompareOrdinal(integerDigits, maxSafeIntegerDigits) <= 0;
    }

    private static CanonicalNumber ParseCanonicalNumber(JsonElement element)
    {
        var raw = element.GetRawText();
        var isNegative = raw[0] == '-';
        var mantissaStart = isNegative ? 1 : 0;
        var exponentMarker = raw.IndexOf('e', mantissaStart);
        if (exponentMarker < 0)
            exponentMarker = raw.IndexOf('E', mantissaStart);
        var mantissaEnd = exponentMarker < 0 ? raw.Length : exponentMarker;
        var decimalPoint = raw.IndexOf('.', mantissaStart, mantissaEnd - mantissaStart);
        var fractionLength = decimalPoint < 0 ? 0 : mantissaEnd - decimalPoint - 1;
        var digits = decimalPoint < 0
            ? raw[mantissaStart..mantissaEnd]
            : raw[mantissaStart..decimalPoint] + raw[(decimalPoint + 1)..mantissaEnd];
        var exponent = exponentMarker < 0
            ? BigInteger.Zero
            : BigInteger.Parse(raw.AsSpan(exponentMarker + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        exponent -= fractionLength;

        digits = digits.TrimStart('0');
        if (digits.Length == 0)
            return new CanonicalNumber(false, "0", BigInteger.Zero);

        var significantLength = digits.Length;
        while (significantLength > 0 && digits[significantLength - 1] == '0')
            significantLength--;
        exponent += digits.Length - significantLength;
        digits = digits[..significantLength];

        return new CanonicalNumber(isNegative, digits, exponent);
    }

    private static string FormatProtocolTimestamp(DateTimeOffset timestamp) =>
        timestamp.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    private readonly record struct CanonicalNumber(bool IsNegative, string Digits, BigInteger Exponent);

    private sealed class CountingWriteStream : Stream
    {
        public long BytesWritten { get; private set; }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => BytesWritten;

        public override long Position
        {
            get => BytesWritten;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => AddBytes(count);

        public override void Write(ReadOnlySpan<byte> buffer) => AddBytes(buffer.Length);

        public override void WriteByte(byte value) => AddBytes(1);

        private void AddBytes(int count) => BytesWritten = checked(BytesWritten + count);
    }
}
