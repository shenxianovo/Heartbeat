using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Heartbeat.Core.DTOs.Segments;
using Heartbeat.Collection.Hub.Ingest;

namespace Heartbeat.Collection.Hub.Collectors.Runtime;

internal interface ISegmentFactProjector
{
    bool Supports(string schemaId, int schemaMajor);

    Guid ProjectedId(Guid streamId, Guid factId);

    bool TryProject(
        FactStreamState stream,
        Guid factId,
        DateTimeOffset start,
        DateTimeOffset end,
        JsonElement payload,
        out ActivitySegmentItem? item);
}

internal sealed class ActivitySegmentFactProjector(ICollectorAppHintResolver? appHintResolver) : ISegmentFactProjector
{
    public bool Supports(string schemaId, int schemaMajor) =>
        schemaMajor == 1 && schemaId is
            "heartbeat.reference.segment" or
            "heartbeat.system.foreground-segment" or
            "heartbeat.browser.active-tab-segment";

    public Guid ProjectedId(Guid streamId, Guid factId)
    {
        var identity = Encoding.ASCII.GetBytes($"{streamId:D}/{factId:D}");
        var value = (factId.ToString("N")[..12] +
                     Convert.ToHexStringLower(SHA256.HashData(identity).AsSpan(0, 10))).ToCharArray();
        value[12] = '7';
        value[16] = "89ab"[Convert.ToInt32(value[16].ToString(), 16) & 0b11];
        return Guid.ParseExact(new string(value), "N");
    }

    public bool TryProject(
        FactStreamState stream,
        Guid factId,
        DateTimeOffset start,
        DateTimeOffset end,
        JsonElement payload,
        out ActivitySegmentItem? item)
    {
        item = null;
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("identityKey", out var identityKey) ||
            identityKey.ValueKind != JsonValueKind.String)
            return false;

        var isBrowser = stream.SchemaId == "heartbeat.browser.active-tab-segment";
        var appIdentityKey = isBrowser &&
                             stream.Dimensions.TryGetValue("appHint", out var appHint) &&
                             appHintResolver?.Resolve(appHint) is
                             { Kind: CollectorAppHintResolutionKind.Resolved } resolution
            ? resolution.AppIdentityKey
            : StringProperty(payload, "appIdentityKey");
        JsonElement? attributes = isBrowser &&
                         payload.TryGetProperty("attributes", out var browserAttributes) &&
                         browserAttributes.ValueKind == JsonValueKind.Object
            ? browserAttributes.Clone()
            : stream.SchemaId == "heartbeat.system.foreground-segment"
                ? null
                : payload.Clone();

        item = new ActivitySegmentItem
        {
            Id = ProjectedId(stream.StreamId, factId),
            Source = stream.Source,
            IdentityKey = identityKey.GetString()!,
            Title = StringProperty(payload, "title"),
            AppIdentityKey = appIdentityKey,
            AppDisplayName = StringProperty(payload, "appDisplayName"),
            StartTime = start,
            EndTime = end,
            Attributes = attributes
        };
        return true;
    }

    private static string? StringProperty(JsonElement payload, string propertyName) =>
        payload.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
