using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Heartbeat.Core.Facts;

public sealed class FactSchemaException(string message, Exception? innerException = null)
    : ArgumentException(message, innerException);

/// <summary>One executable document contract for Package admission and Analytics ingest.</summary>
public sealed record FactSchemaContract(
    string SchemaId, int SchemaMajor, int SchemaRevision, string FactKind,
    string EvolutionMode, IReadOnlyList<string> MutablePayloadPaths,
    JsonElement PayloadSchema)
{
    public static FactSchemaContract Parse(ReadOnlyMemory<byte> content, string expectedId,
        int expectedMajor, int expectedRevision, string expectedFactKind)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            return Parse(document, expectedId, expectedMajor, expectedRevision, expectedFactKind);
        }
        catch (FactSchemaException) { throw; }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new FactSchemaException("Invalid Fact Schema document.", exception);
        }
    }

    private static FactSchemaContract Parse(JsonDocument document, string expectedId,
        int expectedMajor, int expectedRevision, string expectedFactKind)
    {
        var schema = document.RootElement;
        RejectDuplicateObjectKeys(schema, $"Fact Schema Document '{expectedId}'");
        RequireObject(
            schema,
            $"Fact Schema Document '{expectedId}'",
            ["documentVersion", "schemaId", "schemaMajor", "schemaRevision", "factKind", "evolution", "payloadSchemaDialect", "payloadSchema"],
            ["documentVersion", "schemaId", "schemaMajor", "schemaRevision", "factKind", "evolution", "payloadSchemaDialect", "payloadSchema"]);

        var dialect = ReadNonEmptyString(schema, "payloadSchemaDialect", $"Fact Schema Document '{expectedId}'");
        if (dialect != "https://json-schema.org/draft/2020-12/schema")
            throw new FactSchemaException(
                $"Fact Schema Document '{expectedId}' must use JSON Schema Draft 2020-12.");
        if (ReadPositiveInt(schema, "documentVersion", $"Fact Schema Document '{expectedId}'") != 1)
            throw new FactSchemaException($"Fact Schema Document '{expectedId}' has an unsupported documentVersion.");

        var schemaId = ReadNonEmptyString(schema, "schemaId", $"Fact Schema Document '{expectedId}'");
        var major = ReadPositiveInt(schema, "schemaMajor", $"Fact Schema Document '{expectedId}'");
        var revision = ReadPositiveInt(schema, "schemaRevision", $"Fact Schema Document '{expectedId}'");
        var factKind = ReadNonEmptyString(schema, "factKind", $"Fact Schema Document '{expectedId}'");
        if (schemaId != expectedId || major != expectedMajor || revision != expectedRevision || factKind != expectedFactKind)
            throw new FactSchemaException(
                $"Fact Schema Document '{expectedId}' identity or FactKind does not match its Manifest reference.");

        var evolution = schema.GetProperty("evolution");
        RequireObject(
            evolution,
            $"Fact Schema Document '{expectedId}' evolution",
            ["mode", "mutablePayloadPaths"],
            ["mode"]);
        var mode = ReadNonEmptyString(evolution, "mode", $"Fact Schema Document '{expectedId}' evolution");
        if (!EvolutionMatches(factKind, mode))
            throw new FactSchemaException(
                $"Fact Schema Document '{expectedId}' uses evolution mode '{mode}' for FactKind '{factKind}'.");
        IReadOnlyList<string> mutablePayloadPaths = [];
        if (mode == "mutableEvent")
        {
            if (!evolution.TryGetProperty("mutablePayloadPaths", out _))
                throw new FactSchemaException(
                    $"Fact Schema Document '{expectedId}' mutableEvent evolution requires mutablePayloadPaths.");
            mutablePayloadPaths = ReadStringArray(
                evolution,
                "mutablePayloadPaths",
                $"Fact Schema Document '{expectedId}' evolution");
            if (mutablePayloadPaths.Any(path => !IsJsonPointer(path)))
                throw new FactSchemaException(
                    $"Fact Schema Document '{expectedId}' mutablePayloadPaths must contain JSON Pointers.");
        }
        else if (evolution.TryGetProperty("mutablePayloadPaths", out _))
        {
            throw new FactSchemaException(
                $"Fact Schema Document '{expectedId}' only permits mutablePayloadPaths for mutableEvent evolution.");
        }

        var payloadSchema = schema.GetProperty("payloadSchema");
        if (payloadSchema.ValueKind is not (JsonValueKind.Object or JsonValueKind.True or JsonValueKind.False))
            throw new FactSchemaException(
                $"Fact Schema Document '{expectedId}' payloadSchema must be a JSON Schema object or boolean.");

        ValidateSchemaReferences(payloadSchema, payloadSchema, expectedId, dialect);
        return new(schemaId, major, revision, factKind, mode, mutablePayloadPaths, payloadSchema.Clone());
    }

    private static void ValidateSchemaReferences(
        JsonElement resourceRoot,
        JsonElement schema,
        string schemaId,
        string dialect)
    {
        if (schema.ValueKind != JsonValueKind.Object)
            return;

        if (schema.TryGetProperty("$id", out _))
            resourceRoot = schema;

        foreach (var property in schema.EnumerateObject())
        {
            if (property.Name is "$ref" or "$dynamicRef")
            {
                if (property.Value.ValueKind != JsonValueKind.String)
                    throw new FactSchemaException(
                        $"Fact Schema Document '{schemaId}' payloadSchema {property.Name} must be a string.");
                var reference = property.Value.GetString()!;
                if (!reference.StartsWith('#'))
                    throw new FactSchemaException(
                        $"Fact Schema Document '{schemaId}' payloadSchema must be self-contained; external references are not allowed.");
                if (!LocalSchemaReferenceResolves(resourceRoot, reference))
                    throw new FactSchemaException(
                        $"Fact Schema Document '{schemaId}' payloadSchema local reference '{reference}' cannot be resolved.");
            }
            if (property.Name == "$schema" &&
                (property.Value.ValueKind != JsonValueKind.String || property.Value.GetString() != dialect))
                throw new FactSchemaException(
                    $"Fact Schema Document '{schemaId}' payloadSchema $schema must match payloadSchemaDialect.");
        }

        foreach (var subschema in EnumerateSubschemas(schema))
            ValidateSchemaReferences(resourceRoot, subschema, schemaId, dialect);
    }

    private static IEnumerable<JsonElement> EnumerateSubschemas(JsonElement schema)
    {
        foreach (var property in schema.EnumerateObject())
        {
            if (property.Name is
                "additionalItems" or
                "additionalProperties" or
                "contains" or
                "contentSchema" or
                "else" or
                "if" or
                "items" or
                "not" or
                "propertyNames" or
                "then" or
                "unevaluatedItems" or
                "unevaluatedProperties")
            {
                if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.True or JsonValueKind.False)
                    yield return property.Value;
                continue;
            }

            if (property.Name is "allOf" or "anyOf" or "oneOf" or "prefixItems")
            {
                if (property.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var child in property.Value.EnumerateArray())
                        yield return child;
                }
                continue;
            }

            if (property.Name is "$defs" or "definitions" or "dependentSchemas" or "patternProperties" or "properties")
            {
                if (property.Value.ValueKind == JsonValueKind.Object)
                {
                    foreach (var child in property.Value.EnumerateObject())
                        yield return child.Value;
                }
                continue;
            }

            if (property.Name == "dependencies" && property.Value.ValueKind == JsonValueKind.Object)
            {
                foreach (var child in property.Value.EnumerateObject())
                {
                    if (child.Value.ValueKind is JsonValueKind.Object or JsonValueKind.True or JsonValueKind.False)
                        yield return child.Value;
                }
            }
        }
    }

    private static bool LocalSchemaReferenceResolves(JsonElement root, string reference)
    {
        string fragment;
        try
        {
            fragment = Uri.UnescapeDataString(reference[1..]);
        }
        catch (UriFormatException)
        {
            return false;
        }

        if (fragment.Length == 0)
            return true;
        if (fragment[0] != '/')
            return ContainsSchemaAnchor(root, fragment);

        var current = root;
        foreach (var encodedToken in fragment[1..].Split('/'))
        {
            if (!TryDecodeJsonPointerToken(encodedToken, out var token))
                return false;
            if (current.ValueKind == JsonValueKind.Object)
            {
                if (!current.TryGetProperty(token, out current))
                    return false;
            }
            else if (current.ValueKind == JsonValueKind.Array)
            {
                if (!int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var index) ||
                    index < 0 || index >= current.GetArrayLength())
                    return false;
                current = current[index];
            }
            else
            {
                return false;
            }
        }
        return current.ValueKind is JsonValueKind.Object or JsonValueKind.True or JsonValueKind.False;
    }

    private static bool ContainsSchemaAnchor(JsonElement schema, string anchor)
    {
        if (schema.ValueKind != JsonValueKind.Object)
            return false;

        foreach (var property in schema.EnumerateObject())
        {
            if (property.Name is "$anchor" or "$dynamicAnchor" &&
                property.Value.ValueKind == JsonValueKind.String &&
                property.Value.GetString() == anchor)
                return true;
        }

        foreach (var subschema in EnumerateSubschemas(schema))
        {
            if (subschema.ValueKind == JsonValueKind.Object && subschema.TryGetProperty("$id", out _))
                continue;
            if (ContainsSchemaAnchor(subschema, anchor))
                return true;
        }
        return false;
    }

    private static bool TryDecodeJsonPointerToken(string encoded, out string token)
    {
        var builder = new StringBuilder(encoded.Length);
        for (var index = 0; index < encoded.Length; index++)
        {
            if (encoded[index] != '~')
            {
                builder.Append(encoded[index]);
                continue;
            }
            if (++index >= encoded.Length || encoded[index] is not ('0' or '1'))
            {
                token = string.Empty;
                return false;
            }
            builder.Append(encoded[index] == '0' ? '~' : '/');
        }
        token = builder.ToString();
        return true;
    }

    private static void RejectDuplicateObjectKeys(JsonElement element, string context)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new FactSchemaException(
                        $"{context} contains duplicate field '{property.Name}'.");
                RejectDuplicateObjectKeys(property.Value, context);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                RejectDuplicateObjectKeys(item, context);
        }
    }

    private static bool IsJsonPointer(string value)
    {
        if (!value.StartsWith('/'))
            return false;
        for (var index = 1; index < value.Length; index++)
        {
            if (value[index] != '~')
                continue;
            if (++index >= value.Length || value[index] is not ('0' or '1'))
                return false;
        }
        return true;
    }

    private static bool EvolutionMatches(string factKind, string mode) => factKind switch
    {
        "segment" => mode == "segmentSnapshot",
        "event" => mode is "immutableEvent" or "mutableEvent",
        "measurement" => mode == "measurementCorrection",
        _ => false
    };

    private static void RequireObject(
        JsonElement element,
        string context,
        IReadOnlyCollection<string>? allowed,
        IReadOnlyCollection<string> required)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new FactSchemaException($"{context} must be an object.");

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!names.Add(property.Name))
                throw new FactSchemaException($"{context} contains duplicate field '{property.Name}'.");
            if (allowed is not null && !allowed.Contains(property.Name))
                throw new FactSchemaException($"{context} contains unknown field '{property.Name}'.");
        }
        foreach (var name in required.Where(name => !names.Contains(name)))
            throw new FactSchemaException($"{context} is missing required field '{name}'.");
    }

    private static string ReadNonEmptyString(JsonElement parent, string name, string context)
    {
        var value = parent.GetProperty(name);
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new FactSchemaException($"{context}.{name} must be a non-empty string.");
        return value.GetString()!;
    }

    private static int ReadPositiveInt(JsonElement parent, string name, string context)
    {
        var value = parent.GetProperty(name);
        if (!value.TryGetInt32(out var result) || result <= 0)
            throw new FactSchemaException($"{context}.{name} must be a positive integer.");
        return result;
    }

    private static IReadOnlyList<string> ReadStringArray(
        JsonElement parent,
        string name,
        string context,
        bool allowEmpty = false)
    {
        var element = parent.GetProperty(name);
        if (element.ValueKind != JsonValueKind.Array || (!allowEmpty && element.GetArrayLength() == 0))
            throw new FactSchemaException($"{context}.{name} must be a{(allowEmpty ? string.Empty : " non-empty")} array.");
        var values = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()) ||
                values.Contains(item.GetString()!, StringComparer.Ordinal))
                throw new FactSchemaException($"{context}.{name} must contain distinct non-empty strings.");
            values.Add(item.GetString()!);
        }
        return values.ToImmutableArray();
    }

}
