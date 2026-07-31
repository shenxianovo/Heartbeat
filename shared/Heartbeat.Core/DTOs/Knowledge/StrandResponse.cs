namespace Heartbeat.Core.DTOs.Knowledge
{
    /// <summary>Strand 写操作/树读取的节点回读（ADR-031 §2）：稳定 parent ID + 根到自身的 path。</summary>
    public class StrandResponse
    {
        public Guid Id { get; set; }

        /// <summary>null = 顶层。</summary>
        public Guid? ParentStrandId { get; set; }

        public string Name { get; set; } = string.Empty;

        public string Gloss { get; set; } = string.Empty;

        /// <summary>近似有效起点（用户本地叙事日）。null = 向过去无界。</summary>
        public DateOnly? StartedOn { get; set; }

        /// <summary>近似有效终点。null = 可能仍在进行。</summary>
        public DateOnly? EndedOn { get; set; }

        /// <summary>根到自身的名字序列（含自身）。同名不同时期靠日期区分，展示消歧用。</summary>
        public List<string> Path { get; set; } = [];

        /// <summary>并发版本：编辑/移动/结束请求须回传读取时的值（ADR-031 §6）。</summary>
        public long Version { get; set; }

        public List<MatcherDto> Members { get; set; } = [];

        public DateTimeOffset CreatedAt { get; set; }

        public DateTimeOffset UpdatedAt { get; set; }
    }
}
