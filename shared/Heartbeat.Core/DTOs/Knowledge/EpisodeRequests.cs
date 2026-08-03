namespace Heartbeat.Core.DTOs.Knowledge
{
    /// <summary>RecurrenceProbe 生命周期词汇（ADR-031 §5）：active 之外均为已解决，不再重复发问。</summary>
    public static class ProbeStatuses
    {
        public const string Active = "active";
        public const string Promoted = "promoted";
        public const string Denied = "denied";
        public const string Muted = "muted";
    }

    /// <summary>
    /// 新建 Episode（ADR-031 §4）：只承载用户确认后的确定性写——ActivityCluster /
    /// Matcher / Probe 命中都不得调用此路径。近似时间只服务叙事，不是工时。
    /// </summary>
    public class CreateEpisodeRequest
    {
        /// <summary>归属的用户本地叙事日。</summary>
        public DateOnly LocalDate { get; set; }

        /// <summary>用户确认的当天事实，自由文本。</summary>
        public string Text { get; set; } = string.Empty;

        public DateTimeOffset? ApproximateStart { get; set; }

        public DateTimeOffset? ApproximateEnd { get; set; }

        /// <summary>至多一个最具体 Strand（同 Owner）。null = 独立存在。</summary>
        public Guid? RelatedStrandId { get; set; }
    }

    /// <summary>编辑 Episode（按 Id 定位）：日期/文本/近似时间覆盖。关联变更走 Relate。</summary>
    public class UpdateEpisodeRequest
    {
        public long ExpectedVersion { get; set; }

        public DateOnly LocalDate { get; set; }

        public string Text { get; set; } = string.Empty;

        public DateTimeOffset? ApproximateStart { get; set; }

        public DateTimeOffset? ApproximateEnd { get; set; }
    }

    /// <summary>关联 / 解除 Strand：null = 解除。目标必须属于同一 Owner。</summary>
    public class RelateEpisodeRequest
    {
        public long ExpectedVersion { get; set; }

        public Guid? RelatedStrandId { get; set; }
    }

    /// <summary>
    /// 在 Episode 上创建 RecurrenceProbe（ADR-031 §5）：与 Matcher 同形、同 canonicalization
    /// 的路径谓词。同一 Episode 同一 canonical 谓词的活跃 Probe 幂等；已解决则拒绝重开。
    /// </summary>
    public class CreateProbeRequest
    {
        public MatcherDto Matcher { get; set; } = new();
    }

    /// <summary>解决 Probe：denied / muted。promoted 只由提升事务写入。</summary>
    public class ResolveProbeRequest
    {
        public string Resolution { get; set; } = string.Empty;
    }

    /// <summary>
    /// 非破坏性提升（ADR-031 §5）：保留 Episode，新建或选择一个 Strand 并关联；
    /// 可选把 Probe 谓词绑定为该 Strand 的 Matcher 并把 Probe 解决为 promoted。
    /// ExistingStrandId 与 NewStrand 二选一。整个提升是一个事务，任何失败整批回滚。
    /// </summary>
    public class PromoteEpisodeRequest
    {
        /// <summary>Episode 的并发版本。</summary>
        public long ExpectedVersion { get; set; }

        /// <summary>选择已有 Strand（按 Id）。与 NewStrand 互斥。</summary>
        public Guid? ExistingStrandId { get; set; }

        /// <summary>新建 Strand（走与 createStrand 相同的全部校验）。与 ExistingStrandId 互斥。</summary>
        public CreateStrandRequest? NewStrand { get; set; }

        /// <summary>要一并解决（promoted）的 Probe。null = 无 Probe 参与。</summary>
        public Guid? ProbeId { get; set; }

        /// <summary>true 时把 ProbeId 的谓词加入目标 Strand 的 Matcher（canonical 已存在则收敛，不报错）。</summary>
        public bool BindProbeMatcher { get; set; }
    }
}
