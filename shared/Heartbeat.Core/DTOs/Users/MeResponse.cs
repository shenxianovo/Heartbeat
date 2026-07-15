namespace Heartbeat.Core.DTOs.Users
{
    public class MeResponse
    {
        public string Username { get; set; } = string.Empty;
        public bool IsPublic { get; set; }
    }
}
