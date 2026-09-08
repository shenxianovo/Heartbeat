using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace Heartbeat.Core.Facts;

/// <summary>JSON equality and valid numeric range shared by Collector Protocol and Analytics.</summary>
public static class FactJson
{
    public static string? Validate(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name))
                        return $"JSON object contains duplicate key '{property.Name}'.";
                    var propertyError = Validate(property.Value);
                    if (propertyError is not null)
                        return propertyError;
                }
                return null;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    var itemError = Validate(item);
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

    public static string Canonicalize(JsonElement element)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
            WriteCanonical(writer, element);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    public static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
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

    private readonly record struct CanonicalNumber(bool IsNegative, string Digits, BigInteger Exponent);
}
