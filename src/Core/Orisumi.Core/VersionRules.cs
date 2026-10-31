using System.Text.Json;

namespace Orisumi.Core;

public static class VersionRules
{
    public static bool IsUuid7(Guid id) => id.Version == 7 && (id.ToByteArray()[8] & 0xc0) == 0x80;

    public static void Validate(VersionSubmission version, EntitySchema? schema = null)
    {
        Require(IsUuid7(version.EntityId.Value), "ENTITY-2", "EntityId must be a UUIDv7.");
        Require(IsUuid7(version.VersionId.Value), "VERSION-12", "VersionId must be a UUIDv7.");
        Require(version.PreviousVersionId is not { } previous || IsUuid7(previous.Value), "VERSION-12", "Invalid previous VersionId.");
        Require(version.SchemaVersionId is not { } schemaId || IsUuid7(schemaId.Value), "VERSION-12", "Invalid Schema VersionId.");
        Require((version.Content is BusinessContent) == version.SchemaVersionId.HasValue,
            "VERSION-3", "Only business versions must reference a Schema version.");
        switch (version.Content)
        {
            case EntitySchema declaration:
                Require(!string.IsNullOrWhiteSpace(declaration.DisplayName) && !string.IsNullOrWhiteSpace(declaration.Description),
                    "SCHEMA-4", "Schema displayName and description must be nonempty.");
                Require(EntitySchemaRules.ValidResource(declaration.ResourceName), "SCHEMA-6", "Invalid resourceName.");
                if (EntitySchemaRules.FieldViolation(declaration.Fields) is { } rule)
                    throw new ModelViolationException(rule, "Invalid field definitions.");
                break;
            case BusinessContent business:
                Require(schema is not null, "SCHEMA-3", "A saved Schema version is required.");
                if (EntitySchemaRules.EntityViolation(schema!.Fields, business.Fields) is { } contentRule)
                    throw new ModelViolationException(contentRule, "Content does not match its Schema version.");
                break;
            case Observation observation:
                ValidateObservation(observation);
                break;
            case InputSet inputs:
                Require(inputs.Members.Length > 0, "INPUTSET-2", "An InputSet must not be empty.");
                Require(inputs.Members.All(id => IsUuid7(id.Value)), "VERSION-12", "Inputs must be VersionIds.");
                Require(inputs.Members.Distinct().Count() == inputs.Members.Length, "INPUTSET-3", "Inputs must not repeat.");
                break;
            default:
                throw new ModelViolationException("VERSION-3", "Missing or unknown content category.");
        }
    }

    public static void ValidateObservation(Observation observation)
    {
        Require(IsUuid7(observation.ObserverVersionId.Value) && IsUuid7(observation.ContentVersionId.Value),
            "VERSION-12", "Observer and content must be VersionIds.");
        Require(observation.InputSetVersionId is not { } input || IsUuid7(input.Value), "VERSION-12", "Invalid InputSet VersionId.");
        Require(observation.StartAt is null || observation.EndAt is null || observation.StartAt <= observation.EndAt,
            "OBSERVATION-6", "startAt must not be later than endAt.");
        Require(observation.TimeZone is null || TimeZoneInfo.TryConvertIanaIdToWindowsId(observation.TimeZone, out _),
            "OBSERVATION-9", "timeZone must be an IANA time zone or null.");
    }

    public static IReadOnlyList<VersionReference> GetReferences(VersionSubmission version, EntitySchema? schema = null)
    {
        var result = new List<VersionReference>();
        if (version.PreviousVersionId is { } previous) result.Add(new("/previousVersionId", previous));
        if (version.SchemaVersionId is { } schemaId) result.Add(new("/schemaVersionId", schemaId));
        switch (version.Content)
        {
            case Observation observation:
                result.Add(new("/content/observerVersionId", observation.ObserverVersionId));
                result.Add(new("/content/contentVersionId", observation.ContentVersionId));
                if (observation.InputSetVersionId is { } input) result.Add(new("/content/inputSetVersionId", input));
                break;
            case InputSet inputs:
                for (var i = 0; i < inputs.Members.Length; i++) result.Add(new($"/content/members/{i}", inputs.Members[i]));
                break;
            case BusinessContent business:
                Require(schema is not null, "SCHEMA-2", "Reference enumeration needs the version's Schema.");
                CollectObject(schema!.Fields, business.Fields, "/content", result);
                break;
        }
        return result;
    }

    private static void CollectObject(JsonElement fields, JsonElement content, string path, List<VersionReference> result)
    {
        foreach (var field in fields.EnumerateObject())
            if (content.TryGetProperty(field.Name, out var value))
                CollectValue(field.Value, value, path + "/" + field.Name.Replace("~", "~0").Replace("/", "~1"), result);
    }

    private static void CollectValue(JsonElement definition, JsonElement value, string path, List<VersionReference> result)
    {
        if (value.ValueKind == JsonValueKind.Null) return;
        switch (definition.GetProperty("type").GetString())
        {
            case "reference":
                result.Add(new(path, new VersionId(value.GetGuid())));
                break;
            case "object":
                CollectObject(definition.GetProperty("fields"), value, path, result);
                break;
            case "array":
                var index = 0;
                foreach (var item in value.EnumerateArray()) CollectValue(definition.GetProperty("items"), item, $"{path}/{index++}", result);
                break;
        }
    }

    public static async Task<bool> SameBusinessContentAsync(EntitySchema schema, BusinessContent left, BusinessContent right,
        Func<VersionId, Task<EntityId?>> owner)
    {
        var a = System.Text.Json.Nodes.JsonNode.Parse(left.Fields.GetRawText())!;
        var b = System.Text.Json.Nodes.JsonNode.Parse(right.Fields.GetRawText())!;
        await Normalize(a, schema.Fields);
        await Normalize(b, schema.Fields);
        return JsonElement.DeepEquals(JsonSerializer.SerializeToElement(a), JsonSerializer.SerializeToElement(b));

        async Task Normalize(System.Text.Json.Nodes.JsonNode node, JsonElement fields)
        {
            foreach (var field in fields.EnumerateObject())
                if (node[field.Name] is { } value) node[field.Name] = await Value(value, field.Value);
        }
        async Task<System.Text.Json.Nodes.JsonNode?> Value(System.Text.Json.Nodes.JsonNode value, JsonElement definition)
        {
            switch (definition.GetProperty("type").GetString())
            {
                case "reference":
                    return System.Text.Json.Nodes.JsonValue.Create((await owner(new(value.GetValue<Guid>())))?.Value);
                case "object": await Normalize(value, definition.GetProperty("fields")); break;
                case "array":
                    var array = value.AsArray();
                    for (var i = 0; i < array.Count; i++)
                        if (array[i] is { } item) array[i] = await Value(item, definition.GetProperty("items"));
                    break;
            }
            return value.DeepClone();
        }
    }

    public static void Require(bool condition, string rule, string message)
    {
        if (!condition) throw new ModelViolationException(rule, message);
    }
}
