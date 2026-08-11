namespace Heartbeat.Core.DTOs.Apps
{
    public class AppInfoResponse
    {
        public long Id { get; set; }
        public string Key { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        /// <summary>expand 期间的 Dashboard 兼容别名；新消费者使用 DisplayName。</summary>
        public string Name { get; set; } = string.Empty;
    }
}
