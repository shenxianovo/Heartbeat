using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Heartbeat.Observers.Sources.Telemetry;

internal static partial class TelemetryJson
{
    internal static async Task<JsonDocument> Read(SourceReadContext context, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(context.Scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.CaptureKey);
        token.ThrowIfCancellationRequested();
        return await SourceJson.ReadAsync(context.Input, token);
    }

    internal static JsonElement Object(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Expected a JSON object.");
        return value;
    }
    internal static string Text(JsonElement value, string name, bool required = true)
    {
        Object(value);
        if (!value.TryGetProperty(name, out var field))
        {
            if (!required) return "";
            throw new InvalidDataException("Missing " + name);
        }
        if (field.ValueKind != JsonValueKind.String || (required && string.IsNullOrWhiteSpace(field.GetString())))
            throw new InvalidDataException("Invalid string " + name);
        return field.GetString()!;
    }
    internal static JsonElement.ArrayEnumerator Array(JsonElement value, string name, bool required = true)
    {
        Object(value);
        if (!value.TryGetProperty(name, out var field))
        {
            if (required) throw new InvalidDataException("Missing array " + name);
            return JsonSerializer.SerializeToElement(System.Array.Empty<int>()).EnumerateArray();
        }
        if (field.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Invalid array " + name);
        return field.EnumerateArray();
    }
    internal static string Key(params string[] parts) => JsonSerializer.Serialize(parts);
    internal static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    internal static string? Raw(JsonElement value, string name) => value.TryGetProperty(name, out var field) ? SourceJson.Canonical(field) : null;
    internal static object[] Attributes(JsonElement value, params string[] excluded) => Object(value).EnumerateObject().Where(p => !excluded.Contains(p.Name, StringComparer.Ordinal)).OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => (object)new
    {
        name = p.Name, type = p.Value.ValueKind.ToString().ToLowerInvariant(), valueJson = SourceJson.Canonical(p.Value)
    }).ToArray();
    internal static object AttributeField => SourceFields.Array(new { type = "object", nullable = false, fields = new
    {
        name = SourceFields.String(), type = SourceFields.String(), valueJson = SourceFields.String()
    }});
    internal static string Hex(JsonElement value, string name, int length, bool required = true)
    {
        var text = Text(value, name, required);
        if (!required && text.Length == 0) return text;
        if (text.Length != length || text.Any(c => !Uri.IsHexDigit(c)) || text.All(c => c == '0'))
            throw new InvalidDataException("Invalid OTLP " + name);
        return text.ToLowerInvariant();
    }
    internal static (DateTimeOffset? Time, string? Exact) Nanos(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var field)) return (null, null);
        var text = field.ValueKind switch { JsonValueKind.String => field.GetString(), JsonValueKind.Number => field.GetRawText(), _ => null };
        if (!ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var nanos))
            throw new InvalidDataException("Invalid nanosecond time " + name);
        // OTLP explicitly reserves zero for an unknown timestamp. Preserve it in Exact.
        return (nanos == 0 ? null : DateTimeOffset.UnixEpoch.AddTicks(checked((long)(nanos / 100))), text);
    }
    internal static DateTimeOffset? Rfc3339(string? text)
    {
        if (text is null) return null;
        if (!Rfc3339Pattern().IsMatch(text)) throw new InvalidDataException("Invalid RFC3339 time.");
        // Keep original text in properties; truncate sub-tick precision, never round facts forward.
        var normalized = Regex.Replace(text, @"(\.\d{7})\d+", "$1");
        if (!DateTimeOffset.TryParse(normalized, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value))
            throw new InvalidDataException("Invalid RFC3339 time.");
        return value;
    }
    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}[Tt]\d{2}:\d{2}:\d{2}(\.\d+)?([Zz]|[+-]\d{2}:\d{2})$")]
    private static partial Regex Rfc3339Pattern();
}
