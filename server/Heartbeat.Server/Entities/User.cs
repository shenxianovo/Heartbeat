namespace Heartbeat.Server.Entities
{
    public class User
    {
        public string Id { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        public DateTimeOffset LastSeenAt { get; set; }

        // 看板可见性（ADR-025）：false 时匿名按用户名读取一律 404，仅本人（JWT sub）可读。
        public bool IsPublic { get; set; }
    }
}
