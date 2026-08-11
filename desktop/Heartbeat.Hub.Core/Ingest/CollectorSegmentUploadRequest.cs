using Heartbeat.Core.DTOs.Segments;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Heartbeat.Hub.Core.Ingest;

/// <summary>
/// loopback 专用摄入形状。外部 Collector 只能提供平台无关的 AppHint；
/// Analytics 使用的 AppIdentityKey/AppName 不属于此信任边界。
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class CollectorSegmentUploadRequest
{
    public List<CollectorActivitySegmentItem> Segments { get; set; } = [];
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class CollectorActivitySegmentItem
{
    public Guid Id { get; set; }
    public string Source { get; set; } = string.Empty;
    public string IdentityKey { get; set; } = string.Empty;
    public string? AppHint { get; set; }
    /// <summary>
    /// 旧 browser Collector 的本地升级过渡字段。只为避免旧扩展把新版 hub 的 4xx
    /// 当成永久拒收并清空队列；hub 接受但忽略它，绝不写入严格缓存或据此猜 App。
    /// </summary>
    [JsonPropertyName("appName")]
    public string? LegacyAppName { get; set; }
    public string? Title { get; set; }
    public DateTimeOffset StartTime { get; set; }
    public DateTimeOffset EndTime { get; set; }
    public JsonElement? Attributes { get; set; }

    internal ActivitySegmentItem ToActivitySegment(ICollectorAppHintResolver resolver)
    {
        var resolution = string.IsNullOrEmpty(AppHint)
            ? CollectorAppHintResolution.Unknown
            : resolver.Resolve(AppHint);

        return new ActivitySegmentItem
        {
            Id = Id,
            Source = Source,
            IdentityKey = IdentityKey,
            AppIdentityKey = resolution.Kind == CollectorAppHintResolutionKind.Resolved
                ? resolution.AppIdentityKey
                : null,
            AppDisplayName = null,
            AppName = null,
            Title = Title,
            StartTime = StartTime,
            EndTime = EndTime,
            Attributes = Attributes?.Clone()
        };
    }
}
