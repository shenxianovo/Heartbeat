namespace Heartbeat.Core.DTOs.Recaps
{
    /// <summary>每日 Recap 的查询响应（ADR-023 §5）。</summary>
    public class DailyRecapResponse
    {
        public string Date { get; set; } = string.Empty;

        /// <summary>窗口内零 segment：无叙事可讲，前端渲染空态。空日不调 LLM。</summary>
        public bool IsEmpty { get; set; }

        public string? Narrative { get; set; }

        public DateTimeOffset? GeneratedAt { get; set; }

        public string? Model { get; set; }

        /// <summary>
        /// 相关知识已更新，可重新生成（ADR-031 §7）：认证读取历史缓存时确定性重算日期知识投影，
        /// 标识不同（含旧行无标识）即 true。只提示——读取本身不调 LLM，重生成由用户 force 触发。
        /// 公开只读路径恒 false（不重算、不暴露私有知识投影）。
        /// </summary>
        public bool KnowledgeStale { get; set; }
    }
}
