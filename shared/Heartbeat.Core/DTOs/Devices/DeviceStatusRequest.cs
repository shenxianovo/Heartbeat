namespace Heartbeat.Core.DTOs.Devices
{
    public class DeviceStatusRequest
    {
        public string? CurrentAppIdentityKey { get; set; }
        /// <summary>Ticket 05 strict 前保留的展示提示/旧字段。</summary>
        public string CurrentApp { get; set; } = string.Empty;
    }
}
