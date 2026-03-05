namespace Heartbeat.Core.DTOs
{
    public class AppUsageResponse
    {
        public long Id { get; set; }
        public long AppId { get; set; }
        public string AppName { get; set; } = string.Empty;
        public DateTimeOffset StartTime { get; set; }
        public DateTimeOffset EndTime { get; set; }
        public int DurationSeconds { get; set; }
    }
}
