namespace Heartbeat.Core.DTOs.Segments
{
    /// <summary>
    /// 插件采集器段的查询响应（ADR-017）。回放多轨渲染用,不参与统计。
    /// </summary>
    public class SegmentResponse
    {
        public Guid Id { get; set; }

        public string Source { get; set; } = string.Empty;

        public string IdentityKey { get; set; } = string.Empty;

        /// <summary>关联提示:段发生在哪个 App 里。回放挂轨/复用图标用。可空。</summary>
        public long? AppId { get; set; }

        public string? AppName { get; set; }

        public string? Title { get; set; }

        public DateTimeOffset StartTime { get; set; }

        /// <summary>点事件为零长度段(EndTime == StartTime)。</summary>
        public DateTimeOffset EndTime { get; set; }

        public int DurationSeconds { get; set; }

        /// <summary>各 source 自由结构的原始 JSON 文本,由消费方(前端渲染器/LLM)自行解析。</summary>
        public string? Attributes { get; set; }
    }
}
