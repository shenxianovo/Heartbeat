namespace Heartbeat.Server.Entities
{
    /// <summary>
    /// 一段有界的活动记录，由某个采集器（Source）观测并折叠产出。详见 ADR-017。
    /// AppUsage 的泛化形态：系统采集器的段即 Source = 'system'。
    /// </summary>
    public class ActivitySegment
    {
        /// <summary>UUIDv7，由采集端生成，兼作主键与去重键（幂等重传）。</summary>
        public Guid Id { get; set; }

        public long DeviceId { get; set; }

        /// <summary>观测者维度：'system' / 'browser' / 'vscode' / …。统计只消费 'system'（互斥轨）。</summary>
        public string Source { get; set; } = string.Empty;

        /// <summary>采集器声明的"同一个活动"判据，跨批次续接用。system = 规范化 App+Title。</summary>
        public string IdentityKey { get; set; } = string.Empty;

        /// <summary>
        /// expand 阶段保留的旧产品 FK。新事实同时写 AppIdentityId，产品读取必须经
        /// AppIdentity → App；Ticket 04 完成消费者迁移后收缩此列。
        /// </summary>
        public long? AppId { get; set; }

        /// <summary>平台观测身份。system 段必填；插件段可选。</summary>
        public long? AppIdentityId { get; set; }

        public string? Title { get; set; }

        public DateTimeOffset StartTime { get; set; }
        public DateTimeOffset EndTime { get; set; }

        /// <summary>各 Source 自由结构（jsonb）：{url, domain} / {file, project} / …。不参与续接。</summary>
        public string? Attributes { get; set; }

        public Device Device { get; set; } = null!;
        public App? App { get; set; }
        public AppIdentity? AppIdentity { get; set; }
    }
}
