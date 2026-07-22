namespace Heartbeat.Server.Entities
{
    /// <summary>
    /// 采集器上报的观测深度表声明（ADR-030 §4）。全局非 per-Owner——声明是采集器软件的契约,
    /// 不是用户数据（多用户信任面留门,见 ADR-030 Consequences）。
    /// 生效表 = 每 Source 取 max(Version);同 (Source, Version) 重报幂等覆盖。
    /// </summary>
    public class CollectorDeclaration
    {
        public long Id { get; set; }

        public string Source { get; set; } = string.Empty;

        /// <summary>契约版本（深度表变更才递增）,非采集器软件版本。</summary>
        public int Version { get; set; }

        /// <summary>CollectorDeclarationDto 的 canonical JSON（DeclarationValidator.Normalize 后）。</summary>
        public string PayloadJson { get; set; } = string.Empty;

        public DateTimeOffset ReportedAt { get; set; }
    }
}
