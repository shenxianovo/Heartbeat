namespace Heartbeat.Server.Entities
{
    /// <summary>
    /// Segment 面向活动查询的 SQL 结果，不是数据库实体，也不单独持久化。
    /// AppUsage 的泛化形态：系统采集器的段即 Source = 'system'。
    /// </summary>
    public class ActivitySegment
    {
        /// <summary>稳定的查询行身份；历史导入保留旧 Id，原生事实使用其存储键。原生去重以 Owner/Stream/FactId/Revision 为准。</summary>
        public Guid Id { get; set; }
        public string? Aspect { get; set; }
        public Guid? ObserverId { get; set; }
        public string? TargetName { get; set; }
        public string? TargetKind { get; set; }
        public long? TargetId { get; set; }

        public string OwnerId { get; set; } = string.Empty;

        public long? DeviceId { get; set; }

        public Guid StreamId { get; set; }
        public Guid FactId { get; set; }
        public long Revision { get; set; }
        public FactStream Stream { get; set; } = null!;
        /// <summary>Complete Collector Fact payload, without the historical transport wrapper.</summary>
        public string? Payload { get; set; }

        /// <summary>观测者维度：'system' / 'browser' / 'vscode' / …。统计只消费 'system'（互斥轨）。</summary>
        public string Source { get; set; } = string.Empty;

        /// <summary>采集器声明的活动分组判据；不替代 FactId 或 Revision。</summary>
        public string IdentityKey { get; set; } = string.Empty;

        /// <summary>经 AppIdentity → App 查询得到的产品 Id，不是独立存储的外键。</summary>
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
