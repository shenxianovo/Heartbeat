namespace Heartbeat.Agent.Models
{
    public class AgentConfig
    {
        public string ApiBaseUrl { get; set; } = string.Empty;
        public string ApiKey { get; set; } = string.Empty;
        public int UploadIntervalMinutes { get; set; } = 1;
        public int StatusUploadIntervalSeconds { get; set; } = 30;
    }
}
