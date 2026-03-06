namespace Heartbeat.Core.DTOs.Apps
{
    public class IconUploadRequest
    {
        public string AppName { get; set; } = string.Empty;
        public byte[] IconData { get; set; } = [];
    }
}
