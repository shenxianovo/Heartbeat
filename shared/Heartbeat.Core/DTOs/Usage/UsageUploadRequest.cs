namespace Heartbeat.Core.DTOs.Usage
{
    public class UsageUploadRequest
    {
        public List<AppUsageItem> Usages { get; set; } = [];
    }

    public class AppUsageItem
    {
        /// <summary>
        /// 段 Id（UUIDv7），采集端生成，兼作服务端去重键（幂等重传，ADR-017）。
        /// 旧版 Agent 上传无此字段（Guid.Empty），服务端代为生成、不做幂等过滤。
        /// </summary>
        public Guid Id { get; set; }

        public string AppName { get; set; } = string.Empty;

        /// <summary>窗口标题（段级细分维度，可空）。详见 ADR-015。</summary>
        public string? Title { get; set; }

        public DateTimeOffset StartTime { get; set; }

        public DateTimeOffset EndTime { get; set; }
    }
}
