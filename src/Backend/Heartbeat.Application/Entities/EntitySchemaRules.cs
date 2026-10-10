using System.Text.Json;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Heartbeat.Application.Entities;

/// <summary>Validates declarations and values without depending on a storage provider.</summary>
public static partial class EntitySchemaRules
{
    public static bool ValidResource(string value) => ResourcePattern().IsMatch(value)
        && value is not ("schemas" or "observers" or "observations");

    public static bool ValidFields(JsonElement fields) => fields.ValueKind == JsonValueKind.Object
        && fields.EnumerateObject().All(field => field.Name is not ("id" or "__omitted_fields")
            && ValidDefinition(field.Value, true, true));

    private static bool ValidDefinition(JsonElement value, bool field, bool topLevel)
    {
        if (value.ValueKind != JsonValueKind.Object
            || !value.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String
            || !value.TryGetProperty("nullable", out var nullable)
            || nullable.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
        if (field != value.TryGetProperty("required", out var required)
            || field && required.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
        var kind = type.GetString();
        if (kind is not ("string" or "boolean" or "integer" or "number" or "reference" or "object" or "array")
            || kind == "reference" && !topLevel) return false;
        if (value.EnumerateObject().Any(p => p.Name is not ("type" or "nullable" or "required" or "fields" or "items"))) return false;
        if ((kind == "object") != value.TryGetProperty("fields", out var fields)
            || (kind == "array") != value.TryGetProperty("items", out var items)) return false;
        return kind switch
        {
            "object" => fields.ValueKind == JsonValueKind.Object
                && fields.EnumerateObject().All(p => ValidDefinition(p.Value, true, false)),
            "array" => ValidDefinition(items, false, false),
            _ => true,
        };
    }

    public static bool ValidEntity(JsonElement fields, JsonElement references, JsonElement properties)
    {
        if (references.ValueKind != JsonValueKind.Object || properties.ValueKind != JsonValueKind.Object) return false;
        if (references.EnumerateObject().Any(p => !fields.TryGetProperty(p.Name, out var f) || f.GetProperty("type").GetString() != "reference")
            || properties.EnumerateObject().Any(p => !fields.TryGetProperty(p.Name, out var f) || f.GetProperty("type").GetString() == "reference")) return false;
        foreach (var field in fields.EnumerateObject())
        {
            var source = field.Value.GetProperty("type").GetString() == "reference" ? references : properties;
            if (!source.TryGetProperty(field.Name, out var value))
            {
                if (field.Value.GetProperty("required").GetBoolean()) return false;
            }
            else if (!ValidValue(field.Value, value)) return false;
        }
        return true;
    }

    private static bool ValidValue(JsonElement definition, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null) return definition.GetProperty("nullable").GetBoolean();
        return definition.GetProperty("type").GetString() switch
        {
            "string" => value.ValueKind == JsonValueKind.String,
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "integer" => value.ValueKind == JsonValueKind.Number && TryInteger(value, out _),
            "number" => value.ValueKind == JsonValueKind.Number,
            "reference" => value.ValueKind == JsonValueKind.String
                && value.GetString()!.Length == 36
                && Guid.TryParseExact(value.GetString(), "D", out var id) && id.Version == 7
                && (id.ToByteArray()[8] & 0xc0) == 0x80,
            "object" => ValidObject(definition.GetProperty("fields"), value),
            "array" => value.ValueKind == JsonValueKind.Array
                && value.EnumerateArray().All(item => ValidValue(definition.GetProperty("items"), item)),
            _ => false,
        };
    }

    private static bool ValidObject(JsonElement fields, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Any(p => !fields.TryGetProperty(p.Name, out _))) return false;
        foreach (var field in fields.EnumerateObject())
        {
            if (!value.TryGetProperty(field.Name, out var member))
            {
                if (field.Value.GetProperty("required").GetBoolean()) return false;
            }
            else if (!ValidValue(field.Value, member)) return false;
        }
        return true;
    }

    public static bool TryInteger(JsonElement value, out long result)
    {
        result = 0;
        var raw = value.GetRawText();
        var exponentAt = raw.IndexOfAny(['e', 'E']);
        var mantissa = exponentAt < 0 ? raw : raw[..exponentAt];
        var negative = mantissa[0] == '-';
        if (negative) mantissa = mantissa[1..];
        var pointAt = mantissa.IndexOf('.');
        var fractionDigits = pointAt < 0 ? 0 : mantissa.Length - pointAt - 1;
        var digits = mantissa.Replace(".", "").TrimStart('0');
        if (digits.Length == 0) return true;
        if (exponentAt >= 0 && !int.TryParse(raw[(exponentAt + 1)..], NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out _)) return false;
        var exponent = exponentAt < 0 ? 0 : int.Parse(raw[(exponentAt + 1)..], CultureInfo.InvariantCulture);
        var scale = (long)exponent - fractionDigits;
        if (scale < 0)
        {
            var trimmed = digits.TrimEnd('0');
            var removed = Math.Min(-scale, digits.Length - trimmed.Length);
            digits = digits[..(digits.Length - (int)removed)];
            scale += removed;
        }
        if (scale < 0 || digits.Length + scale > 19) return false;
        return long.TryParse((negative ? "-" : "") + digits + new string('0', (int)scale),
            NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out result);
    }

    [GeneratedRegex(@"^[a-z][a-z0-9-]*\z")]
    private static partial Regex ResourcePattern();
}
