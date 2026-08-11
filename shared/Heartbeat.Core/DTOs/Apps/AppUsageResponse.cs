namespace Heartbeat.Core.DTOs.Apps
{
    public class AppUsageResponse
    {
        /// <summary>段 Id（UUIDv7，采集端生成，ADR-017）。</summary>
        public Guid Id { get; set; }
        /// <summary>段所属设备。聚合查询（不传 deviceId）时前端据此分设备泳道。</summary>
        public long DeviceId { get; set; }
        public long AppId { get; set; }
        public string AppKey { get; set; } = string.Empty;
        public string AppDisplayName { get; set; } = string.Empty;
        /// <summary>expand 兼容别名；新消费者使用 AppDisplayName。</summary>
        public string AppName { get; set; } = string.Empty;
        public long? AppIdentityId { get; set; }
        public string? AppIdentityKey { get; set; }
        public string? Title { get; set; }
        public DateTimeOffset StartTime { get; set; }
        public DateTimeOffset EndTime { get; set; }
        public int DurationSeconds { get; set; }
    }
}
