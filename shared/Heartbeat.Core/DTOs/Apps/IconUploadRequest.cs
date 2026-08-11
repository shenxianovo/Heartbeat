namespace Heartbeat.Core.DTOs.Apps
{
    public class IconUploadRequest
    {
        /// <summary>平台观测身份提示；服务端解析到产品 App。</summary>
        public string? AppIdentityKey { get; set; }
        /// <summary>Ticket 05 strict 前保留的 Windows 旧字段。</summary>
        public string AppName { get; set; } = string.Empty;
        public byte[] IconData { get; set; } = [];
        /// <summary>默认 first-valid 稳定；只有显式刷新才替换既有产品图标。</summary>
        public bool Refresh { get; set; }
    }
}
