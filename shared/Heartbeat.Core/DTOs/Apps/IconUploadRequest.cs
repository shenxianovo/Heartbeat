using System.Text.Json.Serialization;

namespace Heartbeat.Core.DTOs.Apps
{
    public class IconUploadRequest
    {
        /// <summary>平台观测身份提示；服务端解析到产品 App。</summary>
        public string? AppIdentityKey { get; set; }
        /// <summary>观测时的可读展示提示；只用于 provisional App 命名。</summary>
        public string? AppDisplayName { get; set; }
        /// <summary>仅用于 strict 边界识别旧 payload；服务端见到该字段即返回 426。</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? AppName { get; set; }
        public byte[] IconData { get; set; } = [];
        /// <summary>默认 first-valid 稳定；只有显式刷新才替换既有产品图标。</summary>
        public bool Refresh { get; set; }
    }
}
