namespace Heartbeat.Core.DTOs.Reports
{
    public class DailyReportResponse
    {
        public string Date { get; set; } = string.Empty;
        public List<AppDurationItem> Apps { get; set; } = [];
    }

    public class AppDurationItem
    {
        public long AppId { get; set; }
        public string AppKey { get; set; } = string.Empty;
        public string AppDisplayName { get; set; } = string.Empty;
        /// <summary>expand 兼容别名；新消费者使用 AppDisplayName。</summary>
        public string AppName { get; set; } = string.Empty;
        public int DurationSeconds { get; set; }
    }
}
