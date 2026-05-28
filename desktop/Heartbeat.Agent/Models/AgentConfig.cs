namespace Heartbeat.Agent.Models
{
    public class AgentConfig
    {
        public string ApiBaseUrl { get; set; } = string.Empty;
        public string ApiKey { get; set; } = string.Empty;
        public string AuthServiceBaseUrl { get; set; } = string.Empty;
        public string DeviceName { get; set; } = string.Empty;

        private int _uploadIntervalMinutes = 1;
        public int UploadIntervalMinutes
        {
            get => _uploadIntervalMinutes;
            set => _uploadIntervalMinutes = value < 1 ? 1 : value;
        }

        private int _statusUploadIntervalSeconds = 30;
        public int StatusUploadIntervalSeconds
        {
            get => _statusUploadIntervalSeconds;
            set => _statusUploadIntervalSeconds = value < 5 ? 5 : value;
        }
    }
}
