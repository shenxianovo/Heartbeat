namespace Heartbeat.Server.Entities
{
    /// <summary>
    /// 某 Owner 某日窗口的叙事摘要缓存（ADR-023 §4）。纯派生物——segments 是事实，
    /// Recap 随时可重生成，故无主动失效：历史窗口命中即回，今日窗口按水位判新鲜。
    /// </summary>
    public class Recap
    {
        public long Id { get; set; }

        public string OwnerId { get; set; } = string.Empty;

        /// <summary>日窗口起点（UTC）。窗口由用户时区切出，时区不同的"同一天"是不同窗口、不同行。</summary>
        public DateTimeOffset WindowStart { get; set; }

        public string Narrative { get; set; } = string.Empty;

        public DateTimeOffset GeneratedAt { get; set; }

        /// <summary>生成所用的 LLM 模型标识。来源诊断用。</summary>
        public string Model { get; set; } = string.Empty;

        /// <summary>提示词模板的内容 hash（SHA-256 前 8 位）。"哪些是旧配方写的"永远可查（ADR-023 §4）。</summary>
        public string PromptHash { get; set; } = string.Empty;

        /// <summary>生成时消费到的最新 segment 时间（裁剪到窗口）。今日缓存的新鲜度水位。</summary>
        public DateTimeOffset SegmentWatermark { get; set; }
    }
}
