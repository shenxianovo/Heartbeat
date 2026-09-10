using System.Text.Json.Serialization;

namespace Heartbeat.Core.DTOs.Segments
{
    /// <summary>
    /// 插件采集器段的查询响应（ADR-017）。回放多轨渲染用,不参与统计。
    /// </summary>
    public class SegmentResponse
    {
        public Guid Id { get; set; }
        public Guid? ObserverId { get; set; }
        public string? TargetKind { get; set; }
        public long? TargetId { get; set; }
        public string? TargetName { get; set; }

        /// <summary>Target 对应的设备维度；未迁移数据暂从旧归属投影。</summary>
        public long? DeviceId { get; set; }

        public string Source { get; set; } = string.Empty;

        public string IdentityKey { get; set; } = string.Empty;

        /// <summary>关联提示:段发生在哪个 App 里。回放挂轨/复用图标用。可空。</summary>
        public long? AppId { get; set; }

        public string? AppKey { get; set; }

        public string? AppDisplayName { get; set; }

        /// <summary>expand 兼容别名；新消费者使用 AppDisplayName。</summary>
        public string? AppName { get; set; }

        /// <summary>原始平台观测身份，和产品 App 投影同时保留。</summary>
        public long? AppIdentityId { get; set; }

        public string? AppIdentityKey { get; set; }

        public string? Title { get; set; }

        public DateTimeOffset StartTime { get; set; }

        /// <summary>点事件为零长度段(EndTime == StartTime)。</summary>
        public DateTimeOffset EndTime { get; set; }

        public int DurationSeconds { get; set; }

        /// <summary>原生 Fact payload，包含 Collector 声明的结构化 attributes。</summary>
        public Dictionary<string, object?>? Payload { get; set; }

        public Guid? StreamId { get; set; }
        public Guid? FactId { get; set; }
        public long? Revision { get; set; }
        /// <summary>native 或 legacy-import；历史导入不表示恢复了原协议身份。</summary>
        public string? Origin { get; set; }
        // Pre-target Browser/VRChat and older clients only; task 05 removes these aliases.
        [JsonPropertyName("subjectId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Guid? LegacySubjectId { get; set; }
        [JsonPropertyName("subjectKind"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? LegacySubjectKind { get; set; }
        [JsonPropertyName("subjectName"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? LegacySubjectName { get; set; }
    }
}
