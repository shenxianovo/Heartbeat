namespace Heartbeat.Core.DTOs
{
    public class IconUploadRequest
    {
        public string ApiKey { get; set; } = string.Empty;
        public byte[] IconData { get; set; } = [];
    }
}
