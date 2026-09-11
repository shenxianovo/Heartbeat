namespace Heartbeat.Server.Entities
{
    /// <summary>
    /// Segment 面向活动查询的 SQL 结果，不是数据库实体，也不单独持久化。
    /// </summary>
    public class ActivitySegment
    {
        /// <summary>稳定的查询行身份；独立观测保留生产者 Id，旧事实保留已有存储 Id。</summary>
        public Guid Id { get; set; }
        public string? Aspect { get; set; }
        public Guid? ObserverId { get; set; }
        public Guid? FoiId { get; set; }
        public ObservationObject? Foi { get; set; }


        public string OwnerId { get; set; } = string.Empty;

        public long? DeviceId { get; set; }

        public Guid? StreamId { get; set; }
        public Guid? FactId { get; set; }
        public long Revision { get; set; }
        public FactStream? Stream { get; set; }
        /// <summary>Complete Collector Fact payload, without the historical transport wrapper.</summary>
        public string? Payload { get; set; }

        public string? Source { get; set; }

        /// <summary>采集器声明的活动分组判据；不替代 FactId 或 Revision。</summary>
        public string IdentityKey { get; set; } = string.Empty;

        /// <summary>从对象图查询得到的产品资料 Id。</summary>
        public long? AppId { get; set; }

        /// <summary>采集时明确提供的平台身份，可空。</summary>
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
