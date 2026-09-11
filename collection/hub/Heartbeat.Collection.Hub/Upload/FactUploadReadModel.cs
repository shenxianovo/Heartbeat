using System.Text.Json;
using Heartbeat.Collection.Hub.Collectors.Runtime;
using Heartbeat.Core.DTOs.Segments;

namespace Heartbeat.Collection.Hub.Upload;

/// <summary>Rebuildable compatibility views for host icons/current activity; never uploaded as segments.</summary>
public static class FactUploadReadModel
{
    public static Guid SegmentId(Guid streamId, Guid factId) =>
        new ActivitySegmentFactProjector().ProjectedId(streamId, factId);

    public static List<ActivitySegmentItem> Segments(IReadOnlyList<FactUploadItem> items) =>
        items.Select(Segment).OfType<ActivitySegmentItem>().ToList();

    public static ActivitySegmentItem? Segment(FactUploadItem item)
    {
        if (item.Observation is { Kind: "segment", Result: { ValueKind: JsonValueKind.Object } result, Start: { } begin, End: { } finish } observation &&
            observation.Aspect is "desktop-activity" or "selected-page" or "account-location" or "activity")
            return new ActivitySegmentItem
            {
                Id = observation.Id, Source = observation.Source ?? string.Empty,
                IdentityKey = Text(result, "identityKey") ?? observation.Id.ToString("D"),
                Title = Text(result, "title"), AppIdentityKey = Text(result, "appIdentityKey"),
                AppDisplayName = Text(result, "appDisplayName"), StartTime = begin, EndTime = finish, Attributes = result.Clone()
            };
        if (item.Stream is null || item.Stream.FactKind != "segment" || item.Fact is not { Payload: { } payload } fact ||
            fact.Start is not { } start || fact.End is not { } end || payload.ValueKind != JsonValueKind.Object)
            return null;
        return new ActivitySegmentItem
        {
            Id = SegmentId(fact.StreamId, fact.FactId),
            Source = item.Stream.Source,
            IdentityKey = Text(payload, "identityKey") ?? fact.FactId.ToString("D"),
            Title = Text(payload, "title"),
            AppIdentityKey = item.Stream.Dimensions.GetValueOrDefault("appIdentityKey") ?? Text(payload, "appIdentityKey"),
            AppDisplayName = Text(payload, "appDisplayName"), StartTime = start, EndTime = end,
            Attributes = payload.Clone()
        };
    }

    private static string? Text(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
