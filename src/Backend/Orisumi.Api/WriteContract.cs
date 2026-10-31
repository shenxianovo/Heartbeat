using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Orisumi.Core;
using Orisumi.Application.Entities;

namespace Orisumi.Api;

internal static class WriteContract
{
    internal static WriteRequest Read(JsonElement body)
    {
        try { return ReadCore(body); }
        catch (Exception exception) when (exception is InvalidOperationException or FormatException or KeyNotFoundException)
        { throw new JsonException("The request does not match the contract.", exception); }
    }

    private static WriteRequest ReadCore(JsonElement body)
    {
        CheckDuplicateMembers(body);
        Members(body, ["observer", "writes"], ["sequence", "schemas"]);
        var observer = Reference(body.GetProperty("observer"));
        CheckStorageValues(body.GetProperty("observer"));
        if (observer.Key is { } observerKey) CheckKeySize(observerKey);
        long? sequence = body.TryGetProperty("sequence", out var seq) ? seq.GetInt64() : null;
        if (sequence is <= 0) throw new JsonException("sequence must be positive.");
        var schemas = new List<SchemaDeclaration>();
        if (body.TryGetProperty("schemas", out var declarations))
            foreach (var declaration in declarations.EnumerateArray())
            {
                Object(declaration, "schemaId", "content");
                var content = declaration.GetProperty("content");
                Object(content, "displayName", "description", "resourceName", "fields");
                Fields(content.GetProperty("fields"));
                CheckStorageValues(declaration);
                schemas.Add(new(new(declaration.GetProperty("schemaId").GetGuid()), (EntitySchema)VersionJson.ReadContent("entity_schema", content)));
            }
        var writes = new List<EntityWrite>();
        foreach (var write in body.GetProperty("writes").EnumerateArray())
        {
            Members(write, ["schemaId", "content"], ["key", "entityId", "time", "inputs"]);
            if (write.TryGetProperty("key", out _) && write.TryGetProperty("entityId", out _)) throw new JsonException("key and entityId are mutually exclusive.");
            var content = write.GetProperty("content");
            if (content.ValueKind != JsonValueKind.Object) throw new JsonException("content must be an object.");
            FactTime? time = null;
            if (write.TryGetProperty("time", out var fact))
            {
                Object(fact, "startAt", "endAt", "timeZone");
                time = new(Time(fact.GetProperty("startAt")), Time(fact.GetProperty("endAt")), fact.GetProperty("timeZone").GetString());
            }
            var inputs = write.TryGetProperty("inputs", out var list)
                ? list.EnumerateArray().Select(v => new VersionId(v.GetGuid())).ToArray() : null;
            var key = write.TryGetProperty("key", out var recognition) ? Key(recognition) : null;
            EntityId? entityId = write.TryGetProperty("entityId", out var entity) ? new(entity.GetGuid()) : null;
            string? storageError = null;
            try
            {
                CheckStorageValues(write);
                if (key is not null) CheckKeySize(key);
            }
            catch (JsonException error) { storageError = error.Message; }
            writes.Add(new(new(write.GetProperty("schemaId").GetGuid()), content.Clone(), entityId, key, time, inputs, storageError));
        }
        return new(observer, writes, schemas, sequence);
    }

    private static DateTimeOffset? Time(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null : VersionJson.ParseTime(value.GetString()!);
    private static EntityReference Reference(JsonElement value)
    {
        if (value.TryGetProperty("entityId", out var entity))
        {
            Object(value, "entityId");
            return new(new(entity.GetGuid()));
        }
        Object(value, "schemaId", "key");
        return new(null, new(value.GetProperty("schemaId").GetGuid()), Key(value.GetProperty("key")));
    }
    private static RecognitionKey Key(JsonElement value)
    {
        Object(value, "scope", "scheme", "value");
        var key = new RecognitionKey(value.GetProperty("scope").GetString(), value.GetProperty("scheme").GetString()!, value.GetProperty("value").GetString()!);
        if (string.IsNullOrEmpty(key.Scheme) || string.IsNullOrEmpty(key.Value)) throw new JsonException("scheme and value must be nonempty strings.");
        return key;
    }
    // The unique index on recognition_keys holds a row of about 2.7 KB.
    private static void CheckKeySize(RecognitionKey key)
    {
        var size = Encoding.UTF8.GetByteCount(key.Scope ?? "") + Encoding.UTF8.GetByteCount(key.Scheme) + Encoding.UTF8.GetByteCount(key.Value);
        if (size > 2048) throw new JsonException("key must not exceed 2048 UTF-8 bytes.");
    }

    private static void Members(JsonElement value, string[] required, string[] optional)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException("Expected an object.");
        foreach (var property in value.EnumerateObject())
            if (!required.Contains(property.Name) && !optional.Contains(property.Name)) throw new JsonException($"Unexpected member: {property.Name}.");
        if (required.Any(name => !value.TryGetProperty(name, out _))) throw new JsonException("Required members are missing.");
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
                        ("string" or "boolean" or "integer" or "number" or "reference" or "binary" or "object" or "array"))
                        throw new JsonException("Invalid field type.");
                    break;
                case "description":
                    if (property.Value.ValueKind != JsonValueKind.String) throw new JsonException("description must be a string.");
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
            if (!remaining.Remove(property.Name)) throw new JsonException($"Unexpected member: {property.Name}.");
        if (remaining.Count != 0) throw new JsonException("Required members are missing.");
    }

    // Ambiguous objects cannot survive a jsonb round trip intact.
    private static void CheckDuplicateMembers(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException("Duplicate JSON member.");
                CheckDuplicateMembers(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) CheckDuplicateMembers(item);
    }

    private static void CheckStorageValues(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in value.EnumerateObject())
                {
                    CheckString(property.Name);
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
