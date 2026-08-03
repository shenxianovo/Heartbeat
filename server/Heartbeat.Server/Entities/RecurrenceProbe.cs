namespace Heartbeat.Server.Entities
{
    /// <summary>
    /// 复现探针（ADR-031 §5）：附在"尚不确定是否持续"的 Episode 上的用户确认谓词。
    /// 复用 Matcher 的路径谓词形状与 canonicalization，但不复用其领域后果——
    /// 命中只产生 Asking 候选，不注入 Recap、不自动建 Strand、不自动关联历史 Episode。
    /// 提升 / 否认 / 静音后进入已解决状态，不再重复发问。
    /// </summary>
    public class RecurrenceProbe
    {
        /// <summary>UUIDv7，应用层生成（ADR-031 §1）。</summary>
        public Guid Id { get; set; }

        public string OwnerId { get; set; } = string.Empty;

        /// <summary>所属 Episode（同 Owner）。Episode 删除时级联删除。</summary>
        public Guid EpisodeId { get; set; }

        public string Source { get; set; } = string.Empty;

        /// <summary>规范化的 [{Reading, Op, Value}] JSON（MatcherNormalizer + MatcherCodec，与 Matcher 同尺）。</summary>
        public string StepsJson { get; set; } = string.Empty;

        /// <summary>active / promoted / denied / muted（RecurrenceProbeStatus）。active 之外均为已解决。</summary>
        public string Status { get; set; } = string.Empty;

        public DateTimeOffset CreatedAt { get; set; }

        /// <summary>解决（提升/否认/静音）时刻。null = 仍活跃。</summary>
        public DateTimeOffset? ResolvedAt { get; set; }

        public Episode Episode { get; set; } = null!;
    }
}
