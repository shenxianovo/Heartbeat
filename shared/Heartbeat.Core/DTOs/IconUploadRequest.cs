namespace Heartbeat.Core.DTOs
{
    public class IconUploadRequest
    {
        public string AppName { get; set; } = string.Empty;
        public byte[] IconData { get; set; } = [];
    }
}
