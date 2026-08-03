namespace Heartbeat.Core.DTOs.Knowledge
{
    /// <summary>发问候选的两种来源（ADR-031 §5/§6）：命中后果不同，不共享生命周期。</summary>
    public static class AskingQuestionKinds
    {
        /// <summary>判官从当日 digest 提出的新活动问题，证据由服务端从真实 segments 物化。</summary>
        public const string Cluster = "cluster";

        /// <summary>RecurrenceProbe 命中：某个未归属 Episode 可能再次出现（确定性生成，零 LLM）。</summary>
        public const string Recurrence = "recurrence";
    }

    /// <summary>证据卡上的一行跨 Source 观察：来自真实 segment 的可核对信息，不是模型推断。</summary>
    public class EvidenceObservationDto
    {
        public string Source { get; set; } = string.Empty;

        /// <summary>该 Source 最浅读数值（应用名 / 站点 / 仓库…）。</summary>
        public string Value { get; set; } = string.Empty;

        /// <summary>时段内该值下最主要的下一深度读数值（可读标题/路径）。null = 无更深读数。</summary>
        public string? Detail { get; set; }

        /// <summary>证据时段内的合计秒数（裁剪到时段）。</summary>
        public double Seconds { get; set; }

        /// <summary>该行是否命中问题锚定的谓词——区分"指纹本体"与"同时段的旁证"。</summary>
        public bool MatchesFingerprint { get; set; }
    }

    /// <summary>
    /// 一张 ActivityCluster 证据卡问题（ADR-031 §6 两阶段第一步）：展示大概时段与跨 Source
    /// 真实观察，让用户用自然语言解释——不再预填最终知识表单。ActivityCluster 是瞬时证据
    /// 视图，不持久化、不声称 Segment 归属。
    /// </summary>
    public class AskingQuestionResponse
    {
        /// <summary>
        /// 问题身份：cluster 为生成时的 UUIDv7（第二阶段凭它取回服务端缓存的证据，
        /// 不接受任意 Segment/Owner ID）；recurrence 恒等于 ProbeId（Owner 校验的库中对象）。
        /// </summary>
        public Guid Id { get; set; }

        /// <summary>cluster / recurrence（AskingQuestionKinds）。</summary>
        public string Kind { get; set; } = string.Empty;

        public string Question { get; set; } = string.Empty;

        /// <summary>锚定谓词（规范化路径谓词）：Mute 与读时 diff 的锚；recurrence 时即 Probe 谓词。</summary>
        public MatcherDto Matcher { get; set; } = new();

        /// <summary>证据时段起点（UTC，命中 segments 的实际跨度，裁剪到日窗口）。</summary>
        public DateTimeOffset? ApproximateStart { get; set; }

        public DateTimeOffset? ApproximateEnd { get; set; }

        /// <summary>时段内的跨 Source 观察行，按时长降序。</summary>
        public List<EvidenceObservationDto> Observations { get; set; } = [];

        /// <summary>recurrence 专属：命中的 Probe。</summary>
        public Guid? ProbeId { get; set; }

        /// <summary>recurrence 专属：Probe 所属 Episode。</summary>
        public Guid? EpisodeId { get; set; }

        /// <summary>recurrence 专属：Episode 的用户原文（帮助回忆"上次是什么"）。</summary>
        public string? EpisodeText { get; set; }

        /// <summary>recurrence 专属：Episode 的本地叙事日。</summary>
        public DateOnly? EpisodeDate { get; set; }
    }

    /// <summary>当日发问集（ADR-031 §6）：证据卡列表，每天封顶。空 = 今天没有值得问的。</summary>
    public class AskingQuestionsResponse
    {
        public List<AskingQuestionResponse> Questions { get; set; } = [];

        /// <summary>读数展示名词典（读数名 → 人话标签，ADR-030 §7），供指纹渲染。</summary>
        public Dictionary<string, string> ReadingLabels { get; set; } = [];
    }
}
