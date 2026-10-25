using System.Globalization;
using System.Numerics;
using System.Text.Json;
using Heartbeat.Core;

namespace Heartbeat.Api;

internal static class SubmissionContract
{
    internal static IReadOnlyList<VersionSubmission> Read(JsonElement body)
    {
        try { return ReadCore(body); }
        catch (Exception exception) when (exception is InvalidOperationException or FormatException or KeyNotFoundException)
        { throw new JsonException("The request does not match the contract.", exception); }
    }

    private static List<VersionSubmission> ReadCore(JsonElement body)
    {
        Object(body, "versions");
        var versions = body.GetProperty("versions");
        if (versions.ValueKind != JsonValueKind.Array || versions.GetArrayLength() == 0)
            throw new JsonException("versions must be a nonempty array.");
        CheckStorageValues(body);
        var result = new List<VersionSubmission>();
        foreach (var version in versions.EnumerateArray())
        {
            Object(version, "entityId", "versionId", "previousVersionId", "category", "schemaVersionId", "content");
            var content = version.GetProperty("content");
            if (content.ValueKind != JsonValueKind.Object) throw new JsonException("content must be an object.");
            switch (version.GetProperty("category").GetString())
            {
                case "observation":
                    Object(content, "observerVersionId", "contentVersionId", "inputSetVersionId", "startAt", "endAt", "timeZone");
                    break;
                case "input_set":
                    Object(content, "members");
                    break;
                case "entity_schema":
                    Object(content, "displayName", "description", "resourceName", "fields");
                    foreach (var name in new[] { "displayName", "description", "resourceName" })
                        if (content.GetProperty(name).ValueKind != JsonValueKind.String) throw new JsonException($"{name} must be a string.");
                    Fields(content.GetProperty("fields"));
                    break;
                case "business": break;
                default: throw new JsonException("Invalid category.");
            }
            try { result.Add(VersionJson.ReadSubmission(version)); }
            catch (Exception exception) when (exception is InvalidOperationException or FormatException or KeyNotFoundException)
            {
                throw new JsonException("A version does not match the contract.", exception);
            }
        }
        return result;
    }

    private static void Fields(JsonElement fields)
    {
        if (fields.ValueKind != JsonValueKind.Object) throw new JsonException("fields must be an object.");
        foreach (var field in fields.EnumerateObject()) Definition(field.Value);
    }

    private static void Definition(JsonElement definition)
    {
        if (definition.ValueKind != JsonValueKind.Object) throw new JsonException("A field definition must be an object.");
        foreach (var property in definition.EnumerateObject())
        {
            switch (property.Name)
            {
                case "type":
                    if (property.Value.ValueKind != JsonValueKind.String || property.Value.GetString() is not
                        ("string" or "boolean" or "integer" or "number" or "reference" or "object" or "array"))
                        throw new JsonException("Invalid field type.");
                    break;
                case "required" or "nullable":
                    if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        throw new JsonException("required and nullable must be booleans.");
                    break;
                case "fields": Fields(property.Value); break;
                case "items": Definition(property.Value); break;
            }
        }
    }

    private static void Object(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException("Expected an object.");
        var remaining = names.ToHashSet(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!remaining.Remove(property.Name)) throw new JsonException($"Unexpected or duplicate member: {property.Name}.");
        if (remaining.Count != 0) throw new JsonException("Required members are missing.");
    }

    private static void CheckStorageValues(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in value.EnumerateObject())
                {
                    CheckString(property.Name);
                    // Ambiguous objects cannot survive a jsonb round trip intact.
                    if (!names.Add(property.Name)) throw new JsonException("Duplicate JSON member.");
                    CheckStorageValues(property.Value);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray()) CheckStorageValues(item);
                break;
            case JsonValueKind.String:
                try { CheckString(value.GetString()!); }
                catch (InvalidOperationException exception) { throw new JsonException("Invalid Unicode string.", exception); }
                break;
            case JsonValueKind.Number:
                CheckNumber(value.GetRawText());
                break;
        }
    }

    private static void CheckString(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\0') throw new JsonException("JSON strings cannot contain U+0000.");
            if (!char.IsSurrogate(value[i])) continue;
            if (!char.IsHighSurrogate(value[i]) || i + 1 == value.Length || !char.IsLowSurrogate(value[++i]))
                throw new JsonException("Invalid Unicode surrogate pair.");
        }
    }

    private static void CheckNumber(string raw)
    {
        var exponentAt = raw.IndexOfAny(['e', 'E']);
        var mantissa = (exponentAt < 0 ? raw : raw[..exponentAt]).TrimStart('-');
        var exponent = exponentAt < 0 ? BigInteger.Zero
            : BigInteger.Parse(raw[(exponentAt + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        var point = mantissa.IndexOf('.');
        var fraction = point < 0 ? 0 : mantissa.Length - point - 1;
        var digits = mantissa.Replace(".", "").TrimStart('0');
        if (exponent > 131072 || exponent < -16383 || fraction - exponent > 16383
            || digits.Length != 0 && digits.Length - fraction + exponent > 131072)
            throw new JsonException("Number exceeds the PostgreSQL numeric range.");
    }
}
