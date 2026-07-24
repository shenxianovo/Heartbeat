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

        /// <summary>
        /// 采集器上报的观测深度表声明原文 JSON（ADR-030 §3）。hub 不解析语义，只作运输与
        /// 持久化（离线期声明不丢）；schema 校验归服务端。未上报为 null。
        /// </summary>
        public string? DeclarationJson { get; set; }

        /// <summary>声明的契约版本（从声明 JSON 提取缓存）：同 source 同版本重报不改写、不触发上行。</summary>
        public int? DeclarationVersion { get; set; }
    }
}
