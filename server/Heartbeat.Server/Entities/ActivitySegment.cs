namespace Heartbeat.Server.Entities
{
    /// <summary>
    /// Segment Fact 面向活动查询的读投影，由 Fact Store 同事务维护（ADR-054）。
    /// AppUsage 的泛化形态：系统采集器的段即 Source = 'system'。
    /// </summary>
    public class ActivitySegment
    {
        /// <summary>稳定的查询行身份；历史导入保留旧 Id，原生事实使用其存储键。原生去重以 Owner/Stream/FactId/Revision 为准。</summary>
        public Guid Id { get; set; }

        public string OwnerId { get; set; } = string.Empty;

        public long? DeviceId { get; set; }

        public Guid? FactKey { get; set; }
        public ObservedFact? Fact { get; set; }
        /// <summary>Complete Collector Fact payload, without the historical transport wrapper.</summary>
        public string? Payload { get; set; }

        /// <summary>观测者维度：'system' / 'browser' / 'vscode' / …。统计只消费 'system'（互斥轨）。</summary>
        public string Source { get; set; } = string.Empty;

        /// <summary>采集器声明的活动分组判据；不替代 FactId 或 Revision。</summary>
        public string IdentityKey { get; set; } = string.Empty;

        /// <summary>
        /// expand 阶段保留的旧产品 FK。新事实同时写 AppIdentityId，产品读取必须经
        /// AppIdentity → App；Ticket 05 strict 收缩旧摄入契约后再评估删除此列。
        /// </summary>
        public long? AppId { get; set; }

        /// <summary>平台观测身份。system 段必填；插件段可选。</summary>
        public long? AppIdentityId { get; set; }

        public string? Title { get; set; }

        public DateTimeOffset StartTime { get; set; }
        public DateTimeOffset EndTime { get; set; }

        /// <summary>历史导入与旧 Matcher 槽位的兼容读字段，派生自 Payload.attributes；不再承载完整 Fact。</summary>
        public string? Attributes { get; set; }

        public Device? Device { get; set; }
        public App? App { get; set; }
        public AppIdentity? AppIdentity { get; set; }
    }
}
