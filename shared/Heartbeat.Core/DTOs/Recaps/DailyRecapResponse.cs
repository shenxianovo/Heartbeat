namespace Heartbeat.Core.DTOs.Recaps
{
    /// <summary>每日 Recap 的查询响应（ADR-023 §5）。</summary>
    public class DailyRecapResponse
    {
        public string Date { get; set; } = string.Empty;

        /// <summary>窗口内零 segment：无叙事可讲，前端渲染空态。空日不调 LLM。</summary>
        public bool IsEmpty { get; set; }

        public string? Narrative { get; set; }

        public DateTimeOffset? GeneratedAt { get; set; }

        public string? Model { get; set; }
    }
}
