namespace Heartbeat.Core.DTOs.Knowledge
{
    /// <summary>
    /// 新建 Strand（ADR-031 §2）：显式父级（null = 顶层），近似本地日期范围可空 = 无界。
    /// 归入既有节点必须按 Id 走 Update——按名收敛的旧语义已退役（ADR-031 迁移）。
    /// </summary>
    public class CreateStrandRequest
    {
        public string Name { get; set; } = string.Empty;

        public string Gloss { get; set; } = string.Empty;

        /// <summary>null = 顶层。父级必须属于同一 Owner。</summary>
        public Guid? ParentStrandId { get; set; }

        /// <summary>近似有效起点（用户本地叙事日）。null = 向过去无界。</summary>
        public DateOnly? StartedOn { get; set; }

        /// <summary>近似有效终点。null = 可能仍在进行。</summary>
        public DateOnly? EndedOn { get; set; }

        public List<MatcherDto> Members { get; set; } = [];
    }

    /// <summary>
    /// 编辑 Strand（按 Id 定位，路由携带）：名字/释义/日期覆盖，成员整组替换。
    /// ExpectedVersion 是读取时的并发版本——陈旧提案返回冲突，不 last-write-wins（ADR-031 §6）。
    /// </summary>
    public class UpdateStrandRequest
    {
        public long ExpectedVersion { get; set; }

        public string Name { get; set; } = string.Empty;

        public string Gloss { get; set; } = string.Empty;

        public DateOnly? StartedOn { get; set; }

        public DateOnly? EndedOn { get; set; }

        public List<MatcherDto> Members { get; set; } = [];
    }

    /// <summary>
    /// 移动 Strand = 纠正过去的错误理解（ADR-031 §2）：改写该节点及后代的历史层级解释。
    /// 现实归属变化不走这里——结束旧节点、在新父级下创建新节点。
    /// </summary>
    public class MoveStrandRequest
    {
        public long ExpectedVersion { get; set; }

        /// <summary>null = 移到顶层。不得为自身或后代。</summary>
        public Guid? NewParentStrandId { get; set; }
    }

    /// <summary>结束 Strand：设定近似终点。仍有活跃子节点时返回冲突，不静默级联（ADR-031 §2）。</summary>
    public class EndStrandRequest
    {
        public long ExpectedVersion { get; set; }

        public DateOnly EndedOn { get; set; }
    }
}
