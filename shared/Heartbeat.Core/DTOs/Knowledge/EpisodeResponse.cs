namespace Heartbeat.Core.DTOs.Knowledge
{
    /// <summary>Episode 回读（ADR-031 §4）：含关联 Strand 的根到节点 path（展示语境用）与探针清单。</summary>
    public class EpisodeResponse
    {
        public Guid Id { get; set; }

        public DateOnly LocalDate { get; set; }

        public string Text { get; set; } = string.Empty;

        public DateTimeOffset? ApproximateStart { get; set; }

        public DateTimeOffset? ApproximateEnd { get; set; }

        /// <summary>null = 独立存在。</summary>
        public Guid? RelatedStrandId { get; set; }

        /// <summary>关联 Strand 的根到自身名字序列；未关联为空。</summary>
        public List<string> RelatedStrandPath { get; set; } = [];

        /// <summary>并发版本：编辑/关联/删除/提升请求须回传读取时的值。</summary>
        public long Version { get; set; }

        public List<ProbeResponse> Probes { get; set; } = [];

        public DateTimeOffset CreatedAt { get; set; }

        public DateTimeOffset UpdatedAt { get; set; }
    }

    /// <summary>RecurrenceProbe 回读（ADR-031 §5）。</summary>
    public class ProbeResponse
    {
        public Guid Id { get; set; }

        public Guid EpisodeId { get; set; }

        public MatcherDto Matcher { get; set; } = new();

        /// <summary>active / promoted / denied / muted（ProbeStatuses）。</summary>
        public string Status { get; set; } = string.Empty;

        public DateTimeOffset CreatedAt { get; set; }

        public DateTimeOffset? ResolvedAt { get; set; }
    }

    /// <summary>提升事务的回读：保留的 Episode（已关联）与目标 Strand。</summary>
    public class PromoteEpisodeResponse
    {
        public EpisodeResponse Episode { get; set; } = new();

        public StrandResponse Strand { get; set; } = new();
    }
}
