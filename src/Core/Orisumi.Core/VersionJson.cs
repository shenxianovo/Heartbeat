using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Orisumi.Core;

/// <summary>The JSON representation shared by persistence and version producers/consumers.</summary>
public static partial class VersionJson
{
    public static JsonElement Content(VersionContent content) => content switch
    {
        BusinessContent business => business.Fields,
        EntitySchema schema => JsonSerializer.SerializeToElement(new
        {
            displayName = schema.DisplayName, description = schema.Description,
            resourceName = schema.ResourceName, fields = schema.Fields,
        }),
        InputSet inputs => JsonSerializer.SerializeToElement(new { members = inputs.Members.Select(id => id.Value) }),
        Observation observation => JsonSerializer.SerializeToElement(new
        {
            observerVersionId = observation.ObserverVersionId.Value,
            contentVersionId = observation.ContentVersionId.Value,
            inputSetVersionId = observation.InputSetVersionId?.Value,
            startAt = observation.StartAt is { } start ? VersionTime.Format(start) : null,
            endAt = observation.EndAt is { } end ? VersionTime.Format(end) : null,
            timeZone = observation.TimeZone,
        }),
        _ => throw new ArgumentException("Unknown content category.", nameof(content)),
    };

    public static JsonElement Submission(VersionSubmission version) => JsonSerializer.SerializeToElement(new
    {
        entityId = version.EntityId.Value, versionId = version.VersionId.Value,
        previousVersionId = version.PreviousVersionId?.Value, category = version.Category,
        schemaVersionId = version.SchemaVersionId?.Value, content = Content(version.Content),
    });

    public static JsonElement Saved(EntityVersion version) => JsonSerializer.SerializeToElement(new
    {
        entityId = version.EntityId.Value, versionId = version.VersionId.Value,
        previousVersionId = version.PreviousVersionId?.Value, category = version.Category,
        schemaVersionId = version.SchemaVersionId?.Value, recordedAt = VersionTime.Format(version.RecordedAt),
        content = Content(version.Content),
    });

    public static VersionSubmission ReadSubmission(JsonElement value) => new(
        new(value.GetProperty("entityId").GetGuid()), new(value.GetProperty("versionId").GetGuid()),
        NullableId(value.GetProperty("previousVersionId")), NullableId(value.GetProperty("schemaVersionId")),
        ReadContent(value.GetProperty("category").GetString()!, value.GetProperty("content")));

    public static EntityVersion ReadSaved(JsonElement value) =>
        new(ReadSubmission(value), ParseTime(value.GetProperty("recordedAt").GetString()!));

    public static VersionContent ReadContent(string category, JsonElement content) => category switch
    {
        "business" => new BusinessContent(content),
        "entity_schema" => new EntitySchema(content.GetProperty("displayName").GetString()!,
            content.GetProperty("description").GetString()!, content.GetProperty("resourceName").GetString()!, content.GetProperty("fields")),
        "input_set" => new InputSet(content.GetProperty("members").EnumerateArray().Select(id => new VersionId(id.GetGuid()))),
        "observation" => new Observation(new(content.GetProperty("observerVersionId").GetGuid()),
            new(content.GetProperty("contentVersionId").GetGuid()), NullableId(content.GetProperty("inputSetVersionId")),
            NullableTime(content.GetProperty("startAt")), NullableTime(content.GetProperty("endAt")), content.GetProperty("timeZone").GetString()),
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
