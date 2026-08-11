using System.Text.Json.Serialization;

namespace Heartbeat.Core.DTOs.Devices
{
    public class DeviceStatusRequest
    {
        public string? CurrentAppIdentityKey { get; set; }
        public string? CurrentAppDisplayName { get; set; }
        /// <summary>仅用于 strict 边界识别旧 payload；服务端见到该字段即返回 426。</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? CurrentApp { get; set; }
    }
}
