namespace Heartbeat.Agent.Models
{
    /// <summary>
    /// 采集器注册表条目（ADR-026）：hub 自动发现后持久化于 config.json 的 collectors。
    /// </summary>
    public class CollectorEntry
    {
        /// <summary>用户开关（WPF）。新发现的采集器默认启用；false 时 hub 对其 POST 返回 403（强制层停用）。</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>采集器自报的 flush 周期（毫秒）；Active 窗口 = 3× 此值。未报为 null，回落默认常量。</summary>
        public int? FlushPeriodMs { get; set; }
    }
}
