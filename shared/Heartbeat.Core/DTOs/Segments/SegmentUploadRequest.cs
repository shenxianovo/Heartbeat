using System.Text.Json;
using System.Text.Json.Serialization;

namespace Heartbeat.Core.DTOs.Segments
{
    /// <summary>
    /// Collection → Analytics 的严格段上传形状（ADR-017/020/035）。system 段由内置
    /// Collector 产出；外部 Collector 的 loopback AppHint 先由 hub 平台 adapter 解析为
    /// AppIdentityKey，再与 system 段经同一出网批次上传。
    /// </summary>
    public class SegmentUploadRequest
    {
        public List<ActivitySegmentItem> Segments { get; set; } = [];
    }

    public class ActivitySegmentItem
    {
        /// <summary>UUIDv7，活动开始时由采集器生成，即活动身份（同一活动跨快照同 Id，ADR-018）；空则由枢纽补齐。</summary>
        public Guid Id { get; set; }

        /// <summary>观测者：'system' / 'browser' / 'vscode' / …。'system' 保留给内置采集器，loopback 冒充由枢纽协议层拒收（ADR-020）。</summary>
        public string Source { get; set; } = string.Empty;

        /// <summary>采集器声明的"同一个活动"判据；服务端 upsert 的 identity guard，查询/回放按其分组（ADR-018）。</summary>
        public string IdentityKey { get; set; } = string.Empty;

        /// <summary>
        /// 平台可观测身份（win:/mac:/sys:）。system 段在严格 Analytics 边界必须提供；
        /// 外部 Collector 无法可靠解析 AppHint 时可以为空，段仍保留但不建立 App 关联。
        /// </summary>
        public string? AppIdentityKey { get; set; }

        /// <summary>观测时的可读展示提示；只用于 provisional App 命名，不参与身份判定。</summary>
        public string? AppDisplayName { get; set; }

        /// <summary>仅用于 strict 边界识别旧 payload；服务端见到该字段即返回 426。</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? AppName { get; set; }

        public string? Title { get; set; }

        public DateTimeOffset StartTime { get; set; }

        /// <summary>点事件为零长度段（EndTime == StartTime）。</summary>
        public DateTimeOffset EndTime { get; set; }

        /// <summary>各 source 自由结构，原样落 jsonb。不参与续接。</summary>
        public JsonElement? Attributes { get; set; }
    }
}
