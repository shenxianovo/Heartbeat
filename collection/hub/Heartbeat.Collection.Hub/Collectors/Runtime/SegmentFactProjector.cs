using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Heartbeat.Core.DTOs.Segments;
using Heartbeat.Collection.Hub.Ingest;

namespace Heartbeat.Collection.Hub.Collectors.Runtime;

internal sealed class ActivitySegmentFactProjector(ICollectorAppHintResolver? appHintResolver)
{
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
            identityKey.ValueKind != JsonValueKind.String ||
            identityKey.GetString() is not { } projectedIdentityKey ||
            string.IsNullOrWhiteSpace(projectedIdentityKey))
            return false;

        // 宿主 adapter 能把 stream 上的 appHint 解析成 App Identity 时用它，否则退回 Fact 自报的
        // appIdentityKey。判定只看通用 stream dimension，不认任何具体 Collector 的 schema。
        var appIdentityKey = stream.Dimensions.TryGetValue("appHint", out var appHint) &&
                             appHintResolver?.Resolve(appHint) is
                             { Kind: CollectorAppHintResolutionKind.Resolved } resolution
            ? resolution.AppIdentityKey
            : StringProperty(payload, "appIdentityKey");
        JsonElement? attributes = stream.SchemaId == "heartbeat.system.foreground-segment"
            ? null
            : payload.Clone();

        item = new ActivitySegmentItem
        {
            Id = ProjectedId(stream.StreamId, factId),
            Source = stream.Source,
            IdentityKey = projectedIdentityKey,
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
