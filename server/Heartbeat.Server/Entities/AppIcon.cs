namespace Heartbeat.Server.Entities
{
    public class AppIcon
    {
        public long Id { get; set; }
        public long AppId { get; set; }

        // 图标写权按 owner 隔离（ADR-025）：唯一键 (OwnerId, AppId)，
        // 每个用户只覆盖自己上传的图标，防跨租户涂鸦。OwnerId 来自上传者 JWT sub。
        public string OwnerId { get; set; } = string.Empty;

        public byte[] IconData { get; set; } = [];
        public DateTimeOffset UpdatedAt { get; set; }

        public App App { get; set; } = null!;
    }
}
