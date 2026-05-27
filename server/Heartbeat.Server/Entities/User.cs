namespace Heartbeat.Server.Entities
{
    public class User
    {
        public string Id { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        public DateTimeOffset LastSeenAt { get; set; }
    }
}
