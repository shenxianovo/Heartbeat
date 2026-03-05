namespace Heartbeat.Server.Entities
{
    public class AppIcon
    {
        public long Id { get; set; }
        public long AppId { get; set; }
        public byte[] IconData { get; set; } = [];
        public DateTimeOffset UpdatedAt { get; set; }

        public App App { get; set; } = null!;
    }
}
