using System.Text.Json;

namespace Heartbeat.Core.DTOs.Segments
{
    /// <summary>
    /// 插件采集器段的上传/接收形状（ADR-017）。
    /// 系统采集器仍走 /usage（AppName 必填）；
    /// 本形状面向插件 source（browser / vscode / …），经 Agent 本地枢纽转发。
    /// </summary>
    public class SegmentUploadRequest
    {
        public List<ActivitySegmentItem> Segments { get; set; } = [];
    }

    public class ActivitySegmentItem
    {
        /// <summary>UUIDv7，采集器生成，兼作服务端去重键；空则由枢纽补齐。</summary>
        public Guid Id { get; set; }

        /// <summary>观测者：'browser' / 'vscode' / …。'system' 保留给内置采集器，ingest 拒收。</summary>
        public string Source { get; set; } = string.Empty;

        /// <summary>采集器声明的"同一个活动"判据，服务端跨批次续接用。</summary>
        public string IdentityKey { get; set; } = string.Empty;

        /// <summary>关联提示：段发生在哪个 App 里（进程名），用于回放挂轨/复用图标。可空。</summary>
        public string? AppName { get; set; }

        public string? Title { get; set; }

        public DateTimeOffset StartTime { get; set; }

        /// <summary>点事件为零长度段（EndTime == StartTime）。</summary>
        public DateTimeOffset EndTime { get; set; }

        /// <summary>各 source 自由结构，原样落 jsonb。不参与续接。</summary>
        public JsonElement? Attributes { get; set; }
    }
}
