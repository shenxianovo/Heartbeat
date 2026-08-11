namespace Heartbeat.Core.DTOs.Devices
{
    public class DeviceStatusResponse
    {
        /// <summary>
        /// 在线判定窗口（ADR-021）：规则为窗口 ≥ 2× 心跳间隔，取 3× 抗抖动。
        /// Agent 侧 keepalive 为 30s 代码常量（StatusUploadWorker.KeepaliveInterval）。
        /// </summary>
        public static readonly TimeSpan OnlineWindow = TimeSpan.FromSeconds(90);

        public long Id { get; set; }
        public long? CurrentAppId { get; set; }
        public string? CurrentAppKey { get; set; }
        public string? CurrentAppDisplayName { get; set; }
        public string? CurrentAppIdentityKey { get; set; }
        /// <summary>expand 兼容别名；新消费者使用 CurrentAppDisplayName。</summary>
        public string? CurrentApp { get; set; } = string.Empty;
        public DateTimeOffset? LastSeen { get; set; }
        public bool IsOnline => LastSeen != null &&
                               DateTimeOffset.UtcNow - LastSeen < OnlineWindow;
    }
}
