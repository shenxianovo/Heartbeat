namespace Heartbeat.Core.DTOs.Knowledge
{
    /// <summary>Strand 写操作被拒的可解释错误（ADR-031 §2/§6）：code 机器可判，Strands 携带冲突方（如活跃子节点）。</summary>
    public class KnowledgeErrorResponse
    {
        /// <summary>
        /// 机器可判代码：invalid_name / invalid_dates / parent_not_found / cycle / overlap /
        /// outside_parent_range / children_outside_range / active_children / not_found / version_conflict。
        /// </summary>
        public string Code { get; set; } = string.Empty;

        public string Message { get; set; } = string.Empty;

        /// <summary>冲突涉及的节点（活跃子节点清单 / 日期重叠的同名时期），供 UI 引导用户处理。</summary>
        public List<StrandBriefResponse> Strands { get; set; } = [];
    }

    /// <summary>冲突清单里的节点摘要。</summary>
    public class StrandBriefResponse
    {
        public Guid Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public DateOnly? StartedOn { get; set; }

        public DateOnly? EndedOn { get; set; }
    }
}
