using Heartbeat.Core.DTOs.Segments;

namespace Heartbeat.Core;

/// <summary>
/// 插件段的通用完整性校验（ADR-017）。与 <see cref="UsageValidationPolicy"/> 共用时间阈值；
/// 差异：允许零长度段（点事件），要求 Source/IdentityKey/Id 齐全。
/// 不限制 Source 取值——'system' 冒充的拒收是 Agent 枢纽 loopback 层的职责（ADR-020），策略本身 source 无关。
/// </summary>
public static class SegmentValidationPolicy
{
    public static List<ActivitySegmentItem> Filter(List<ActivitySegmentItem> segments, DateTimeOffset now)
    {
        return segments
            .Where(s => s.Id != Guid.Empty
                     && !string.IsNullOrWhiteSpace(s.Source)
                     && !string.IsNullOrWhiteSpace(s.IdentityKey)
                     && s.StartTime != default
                     && s.EndTime >= s.StartTime
                     && s.StartTime.Year >= UsageValidationPolicy.MinYear
                     && s.EndTime <= now + UsageValidationPolicy.TimeSkewTolerance
                     && s.StartTime >= now - UsageValidationPolicy.TimeSkewTolerance - UsageValidationPolicy.MaxDuration
                     && (s.EndTime - s.StartTime) <= UsageValidationPolicy.MaxDuration)
            .OrderBy(s => s.StartTime)
            .ToList();
    }
}
