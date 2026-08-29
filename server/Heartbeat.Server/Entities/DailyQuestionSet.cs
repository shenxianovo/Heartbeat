namespace Heartbeat.Server.Entities
{
    /// <summary>
    /// 当日发问缓存（ADR-029 §4 / ADR-044 §3）：与 recap 同构——按 (Owner, WindowKey) 一份，
    /// 跟段水位，失败不写。
    /// PayloadJson 为封顶后的证据卡问题列表；读取时对已裁决 Matcher 做确定性 diff 过滤（零 LLM 重调）。
    /// </summary>
    public class DailyQuestionSet
    {
        /// <summary>
        /// 当前 payload 契约版本（ADR-031 §6 两阶段协议）。版本 2 = ActivityCluster 证据卡
        /// （AskingQuestionResponse 列表）。旧单阶段最终表单（隐式版本 0）读取时视为缓存失效
        /// 重新生成，绝不再被客户端当作可直接提交的知识写入。
        /// </summary>
        public const int CurrentPayloadVersion = 2;

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

        /// <summary>生成时消费到的最新 segment 时间（裁剪到窗口）。今日新鲜度水位。</summary>
        public DateTime SegmentWatermark { get; set; }

        public DateTimeOffset GeneratedAt { get; set; }

        /// <summary>payload 契约版本。不等于 CurrentPayloadVersion 的行按缓存未命中处理。</summary>
        public int PayloadVersion { get; set; }

        /// <summary>序列化的 AskingQuestionResponse 列表（cluster 问题；recurrence 问题读时确定性生成，不缓存）。</summary>
        public string PayloadJson { get; set; } = string.Empty;
    }
}
