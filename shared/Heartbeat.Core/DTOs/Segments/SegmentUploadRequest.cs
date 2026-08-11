using System.Text.Json;
using System.Text.Json.Serialization;

namespace Heartbeat.Core.DTOs.Segments
{
    /// <summary>
    /// 段的统一上传/接收形状（ADR-017/020）：POST /segments 是唯一上传入口。
    /// system 段由 Agent 内置采集器进程内产出，插件段（browser / vscode / …）
    /// 经 loopback 汇入枢纽，同一批次上传。
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
        /// 平台可观测身份（win:/mac:/sys:）。expand 阶段可空；为空时 Analytics 暂按
        /// 旧 Windows AppName 生成身份，Ticket 05 strict cutover 后改为必填。
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
