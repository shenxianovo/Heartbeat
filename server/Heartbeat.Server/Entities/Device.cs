namespace Heartbeat.Server.Entities
{
    public class Device
    {
        public long Id { get; set; }
        public string OwnerId { get; set; } = string.Empty;
        public string HardwareId { get; set; } = string.Empty;
        public string DeviceName { get; set; } = string.Empty;
        /// <summary>expand 阶段保留的旧展示快照；权威当前产品经 CurrentAppIdentity 解析。</summary>
        public string CurrentApp { get; set; } = string.Empty;
        public long? CurrentAppIdentityId { get; set; }
        public DateTimeOffset LastSeen { get; set; }

        public AppIdentity? CurrentAppIdentity { get; set; }
    }
}
