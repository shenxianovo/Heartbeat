namespace Heartbeat.Core.DTOs.Recaps
{
    /// <summary>
    /// 每日 Recap 的查询响应（ADR-023 §5，三态随 ADR-042 §3）。
    ///
    /// 读取有三种态，用字段组合隐式表达，不设额外的 notGenerated 布尔：
    /// - <c>IsEmpty = true</c>：窗口内零 segment，无叙事可讲；
    /// - <c>IsEmpty = false</c> 且 <c>Narrative == null</c>：这天有数据，但从未生成过；
    /// - 否则：有叙事，附来源与两个判脏位。
    ///
    /// 读取路径永不调用 LLM、永不写库（ADR-042 §2）：生成只由 POST /recaps/daily/generate 触发。
    /// </summary>
    public class DailyRecapResponse
    {
        public string Date { get; set; } = string.Empty;

        /// <summary>窗口内零 segment：无叙事可讲，前端渲染空态。空日不调 LLM。</summary>
        public bool IsEmpty { get; set; }

        public string? Narrative { get; set; }

        public DateTimeOffset? GeneratedAt { get; set; }

        public string? Model { get; set; }

        /// <summary>
        /// 相关知识已更新，可重新生成（ADR-031 §7）：认证读取时确定性重算日期知识投影，
        /// 标识不同（含旧行无标识）即 true。只提示——读取本身不调 LLM，重生成由用户显式触发。
        /// 公开只读路径恒 false（不重算、不暴露私有知识投影）。
        /// </summary>
        public bool KnowledgeStale { get; set; }

        /// <summary>
        /// 段数据已长出缓存的水位，可重新生成（ADR-042 §3）：今日窗口落后超过服务端阈值即 true。
        /// 与 KnowledgeStale 同构地"只提示"——原先藏在 GET 里的自动重生成已移除，改由前端在
        /// owner 视角下显式触发生成。已结束的历史窗口恒 false；公开只读路径恒 false。
        /// </summary>
        public bool SegmentStale { get; set; }
    }
}
