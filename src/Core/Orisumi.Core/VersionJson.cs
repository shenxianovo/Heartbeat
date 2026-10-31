using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Orisumi.Core;

/// <summary>The JSON representation shared by persistence and version producers/consumers.</summary>
public static partial class VersionJson
{
    public static JsonElement Content(VersionContent content) => content switch
    {
        Observation observation => observation.Fields,
        EntitySchema schema => JsonSerializer.SerializeToElement(new
        {
            displayName = schema.DisplayName, description = schema.Description,
            resourceName = schema.ResourceName, kind = schema.Kind, immutable = schema.Immutable, fields = schema.Fields,
        }),
        InputSet inputs => JsonSerializer.SerializeToElement(new { members = inputs.Members.Select(id => id.Value) }),
        _ => throw new ArgumentException("Unknown content category.", nameof(content)),
    };

    public static JsonElement Saved(EntityVersion version)
    {
        var value = new Dictionary<string, object?>
        {
            ["entityId"] = version.EntityId.Value, ["versionId"] = version.VersionId.Value,
            ["previousVersionId"] = version.PreviousVersionId?.Value, ["category"] = version.Category,
            ["recordedAt"] = VersionTime.Format(version.RecordedAt), ["content"] = Content(version.Content),
        };
        if (version.Content is Observation observation)
        {
            value["schemaVersionId"] = version.SchemaVersionId?.Value;
            value["observerId"] = observation.ObserverId.Value;
            value["observerVersionId"] = observation.ObserverVersionId?.Value;
            value["formation"] = observation.Formation;
            value["inputSetVersionId"] = observation.InputSetVersionId?.Value;
            value["startAt"] = observation.StartAt is { } start ? VersionTime.Format(start) : null;
            value["endAt"] = observation.EndAt is { } end ? VersionTime.Format(end) : null;
            value["timeZone"] = observation.TimeZone;
        }
        return JsonSerializer.SerializeToElement(value);
    }

    public static EntityVersion ReadSaved(JsonElement value) => new(new(
        new(value.GetProperty("entityId").GetGuid()), new(value.GetProperty("versionId").GetGuid()),
        NullableId(value.GetProperty("previousVersionId")),
        value.TryGetProperty("schemaVersionId", out var schema) ? NullableId(schema) : null,
        value.GetProperty("category").GetString() == "observation"
            ? new Observation(new(value.GetProperty("observerId").GetGuid()), NullableId(value.GetProperty("observerVersionId")),
                value.GetProperty("formation").GetString()!, value.GetProperty("content").Clone(),
                NullableId(value.GetProperty("inputSetVersionId")), NullableTime(value.GetProperty("startAt")),
                NullableTime(value.GetProperty("endAt")), value.GetProperty("timeZone").GetString())
            : ReadContent(value.GetProperty("category").GetString()!, value.GetProperty("content"))),
        ParseTime(value.GetProperty("recordedAt").GetString()!));

    public static VersionContent ReadContent(string category, JsonElement content) => category switch
    {
        "entity_schema" => new EntitySchema(content.GetProperty("displayName").GetString()!,
            content.GetProperty("description").GetString()!, content.GetProperty("resourceName").GetString()!,
            content.GetProperty("fields"), content.GetProperty("kind").GetString()!, content.GetProperty("immutable").GetBoolean()),
        "input_set" => new InputSet(content.GetProperty("members").EnumerateArray().Select(id => new VersionId(id.GetGuid()))),
        _ => throw new JsonException("Unknown content category."),
    };

    public static DateTimeOffset ParseTime(string value)
    {
        if (!TimestampPattern().IsMatch(value)) throw new JsonException("Time must include an explicit UTC offset.");
        // Parse only whole microseconds: DateTimeOffset otherwise rounds longer fractions before truncation.
        var dot = value.IndexOf('.');
        if (dot >= 0)
        {
            var end = dot + 1;
            while (end < value.Length && char.IsAsciiDigit(value[end])) end++;
            if (end - dot - 1 > 6) value = value[..(dot + 7)] + value[end..];
        }
        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            throw new JsonException("Invalid timestamp.");
        return VersionTime.Normalize(time);
    }

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}[Tt]\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:[Zz]|[+-]\d{2}:\d{2})\z")]
    private static partial Regex TimestampPattern();

    private static VersionId? NullableId(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null : new(value.GetGuid());
    private static DateTimeOffset? NullableTime(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null : ParseTime(value.GetString()!);
}
