namespace Heartbeat.Server.Entities
{
    /// <summary>
    /// 用户确认的有界事实（ADR-031 §4）：某个本地叙事日里的一次具体发生。
    /// 不在 Strand 树中、不拥有 Segment、不做多对多——至多关联一个最具体 Strand
    /// 并经它获得祖先语境。只由用户确认的写路径创建；ActivityCluster / Matcher /
    /// Probe 命中都不得自动落库（ADR-031 §4 负向边界）。
    /// </summary>
    public class Episode
    {
        /// <summary>UUIDv7，应用层生成（ADR-031 §1）。</summary>
        public Guid Id { get; set; }

        public string OwnerId { get; set; } = string.Empty;

        /// <summary>归属的用户本地叙事日。近似时间跨午夜时仍由它决定"算哪天"。</summary>
        public DateOnly LocalDate { get; set; }

        /// <summary>用户确认的当天事实，自由文本。横切背景写在这里，不建多对多（ADR-031 §4）。</summary>
        public string Text { get; set; } = string.Empty;

        /// <summary>近似起点，只服务叙事——不是工时。null = 未提供。</summary>
        public DateTimeOffset? ApproximateStart { get; set; }

        /// <summary>近似终点，只服务叙事。null = 未提供。</summary>
        public DateTimeOffset? ApproximateEnd { get; set; }

        /// <summary>至多一个最具体 Strand（同 Owner）。null = 独立存在。</summary>
        public Guid? RelatedStrandId { get; set; }

        /// <summary>并发版本：每次成功写 +1。陈旧提案返回冲突而非覆盖（ADR-031 §6）。</summary>
        public long Version { get; set; }

        public DateTimeOffset CreatedAt { get; set; }

        public DateTimeOffset UpdatedAt { get; set; }

        public Strand? RelatedStrand { get; set; }

        public List<RecurrenceProbe> Probes { get; set; } = [];
    }
}
