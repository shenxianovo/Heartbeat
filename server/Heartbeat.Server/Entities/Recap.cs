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

        /// <summary>
        /// Analytics 验证后生成的完整窗口身份。null 只表示 ADR-044 前的 fixed-offset 派生缓存；
        /// legacy 行保留但永远不能命中新窗口。
        /// </summary>
        public string? WindowKey { get; set; }

        public int? WindowVersion { get; set; }

        public string? WindowKind { get; set; }

        public string? LocalDate { get; set; }

        public string? TimeZone { get; set; }

        /// <summary>完整半开 UTC 窗口的起点；legacy 行也保留原值供诊断。</summary>
        public DateTimeOffset WindowStart { get; set; }

        /// <summary>完整半开 UTC 窗口的 exclusive end；legacy fixed-offset 行为 null。</summary>
        public DateTimeOffset? WindowEndExclusive { get; set; }

        public string Narrative { get; set; } = string.Empty;

        public DateTimeOffset GeneratedAt { get; set; }

        /// <summary>生成所用的 LLM 模型标识。来源诊断用。</summary>
        public string Model { get; set; } = string.Empty;

        /// <summary>提示词模板的内容 hash（SHA-256 前 8 位）。"哪些是旧配方写的"永远可查（ADR-023 §4）。</summary>
        public string PromptHash { get; set; } = string.Empty;

        /// <summary>生成时消费到的最新 segment 时间（裁剪到窗口）。今日缓存的新鲜度水位。</summary>
        public DateTimeOffset SegmentWatermark { get; set; }

        /// <summary>
        /// 生成时实际使用的日期知识投影标识（ADR-031 §7）：相关 Strand 祖先链 + 命中 Matcher +
        /// 当日 Episode 的 canonical hash，不是全局知识版本。读取历史时确定性重算比对——不同只提示
        /// 可重新生成，绝不自动调 LLM。null = 旧行（投影引入前生成），惰性视为可重新生成，不批量回填。
        /// </summary>
        public string? KnowledgeHash { get; set; }
    }
}
