using System.Text.Json;
using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

namespace Heartbeat.Core;

/// <summary>Validates declarations and values without depending on a storage provider.</summary>
public static partial class EntitySchemaRules
{
    public static bool ValidResource(string value) => value is not null && ResourcePattern().IsMatch(value);

    public static bool ValidFields(JsonElement fields) => FieldViolation(fields) is null;

    internal static string? FieldViolation(JsonElement fields, bool topLevel = true)
    {
        if (!UniqueProperties(fields)) return "SCHEMA-12";
        foreach (var field in fields.EnumerateObject())
        {
            if (topLevel && field.Name == "id") return "SCHEMA-7";
            if (DefinitionViolation(field.Value, true) is { } violation) return violation;
        }
        return null;
    }

    private static string? DefinitionViolation(JsonElement value, bool field)
    {
        if (!UniqueProperties(value)) return "SCHEMA-12";
        if (!value.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String
            || !value.TryGetProperty("nullable", out var nullable)
            || nullable.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return "SCHEMA-10";
        if (field != value.TryGetProperty("required", out var required)
            || field && required.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return "SCHEMA-10";
        var kind = type.GetString();
        if (kind is not ("string" or "boolean" or "integer" or "number" or "reference" or "object" or "array")) return "SCHEMA-12";
        if (value.EnumerateObject().Any(p => p.Name is not ("type" or "nullable" or "required" or "fields" or "items"))) return "SCHEMA-12";
        if ((kind == "object") != value.TryGetProperty("fields", out var fields)
            || (kind == "array") != value.TryGetProperty("items", out var items)) return "SCHEMA-14";
        return kind switch
        {
            "object" => fields.ValueKind == JsonValueKind.Object ? FieldViolation(fields, false) : "SCHEMA-14",
            "array" => DefinitionViolation(items, false),
            _ => null,
        };
    }

    public static bool ValidEntity(JsonElement fields, JsonElement content) => EntityViolation(fields, content) is null;

    internal static string? EntityViolation(JsonElement fields, JsonElement content) =>
        ValidFields(fields) ? ObjectViolation(fields, content) : "SCHEMA-2";

    private static string? ValueViolation(JsonElement definition, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null)
            return definition.GetProperty("nullable").GetBoolean() ? null : "SCHEMA-11";
        var kind = definition.GetProperty("type").GetString();
        if (kind == "object") return ObjectViolation(definition.GetProperty("fields"), value);
        if (kind == "array") return ArrayViolation(definition.GetProperty("items"), value);
        var valid = kind switch
        {
            "string" => value.ValueKind == JsonValueKind.String,
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "integer" => value.ValueKind == JsonValueKind.Number && IsInteger(value),
            "number" => value.ValueKind == JsonValueKind.Number,
            "reference" => value.ValueKind == JsonValueKind.String
                && value.GetString()!.Length == 36
                && Guid.TryParseExact(value.GetString(), "D", out var id) && VersionRules.IsUuid7(id),
            _ => false,
        };
        return valid ? null : kind == "reference" ? "SCHEMA-9" : "SCHEMA-2";
    }

    private static string? ArrayViolation(JsonElement items, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array) return "SCHEMA-2";
        foreach (var item in value.EnumerateArray())
            if (ValueViolation(items, item) is { } violation) return violation;
        return null;
    }

    private static string? ObjectViolation(JsonElement fields, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return "SCHEMA-2";
        if (!UniqueProperties(value) || value.EnumerateObject().Any(p => !fields.TryGetProperty(p.Name, out _))) return "SCHEMA-12";
        foreach (var field in fields.EnumerateObject())
        {
            if (!value.TryGetProperty(field.Name, out var member))
            {
                if (field.Value.GetProperty("required").GetBoolean()) return "SCHEMA-11";
            }
            else if (ValueViolation(field.Value, member) is { } violation) return violation;
        }
        return null;
    }

    // TryGetProperty and jsonb otherwise keep only the last occurrence, losing evidence.
    internal static bool UniqueProperties(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return false;
        var names = new HashSet<string>(StringComparer.Ordinal);
        return value.EnumerateObject().All(property => names.Add(property.Name));
    }

    public static bool IsInteger(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number) return false;
        var raw = value.GetRawText();
        var exponentAt = raw.IndexOfAny(['e', 'E']);
        var mantissa = exponentAt < 0 ? raw : raw[..exponentAt];
        var pointAt = mantissa.IndexOf('.');
        var fractionDigits = pointAt < 0 ? 0 : mantissa.Length - pointAt - 1;
        var digits = mantissa.Replace(".", "").TrimStart('-', '0');
        if (digits.Length == 0) return true;
        var trailingZeros = digits.Length - digits.TrimEnd('0').Length;
        var exponent = exponentAt < 0 ? BigInteger.Zero
            : BigInteger.Parse(raw[(exponentAt + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        return exponent - fractionDigits + trailingZeros >= 0;
    }

    [GeneratedRegex(@"^[a-z][a-z0-9-]*\z")]
    private static partial Regex ResourcePattern();
}
