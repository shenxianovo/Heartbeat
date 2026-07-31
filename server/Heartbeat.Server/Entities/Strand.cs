namespace Heartbeat.Server.Entities
{
    /// <summary>
    /// 知识层核心对象（ADR-028 §2，层级与日期随 ADR-031）：用户生活里一条有名字的持续叙事脉络。
    /// 严格单父级、无环、无固定层数/类型的树；节点 = 名字 + 自由释义 + 近似有效日期 + 指纹（一组 Matcher）。
    /// 策展层，非派生物——库里只存用户亲口确认的事实（ADR-029 §1）；绝不写回 segment。
    /// </summary>
    public class Strand
    {
        /// <summary>UUIDv7，应用层生成。</summary>
        public Guid Id { get; set; }

        public string OwnerId { get; set; } = string.Empty;

        /// <summary>父节点（ADR-031 §2）：null = 顶层。父可以是零 Matcher 的纯语境容器。</summary>
        public Guid? ParentStrandId { get; set; }

        public string Name { get; set; } = string.Empty;

        /// <summary>lower(trim(Name))。同 Owner、同父、同规范名的有效日期范围不得重叠（ADR-031 §2）。</summary>
        public string NormalizedName { get; set; } = string.Empty;

        /// <summary>自由释义：这个东西在用户自己的话里是什么。绝不加 schema——通用工具、共现模式都写在这里（ADR-029 §5）。</summary>
        public string Gloss { get; set; } = string.Empty;

        /// <summary>近似有效起点（用户本地叙事日）。null = 向过去无界（ADR-031 §2）。</summary>
        public DateOnly? StartedOn { get; set; }

        /// <summary>近似有效终点（用户本地叙事日）。null = 可能仍在进行（向未来无界）。</summary>
        public DateOnly? EndedOn { get; set; }

        /// <summary>并发版本：每次成功写 +1。陈旧提案（教学协议 / 编辑表单）按此返回冲突而非覆盖（ADR-031 §6）。</summary>
        public long Version { get; set; }

        public DateTimeOffset CreatedAt { get; set; }

        /// <summary>名字/释义/成员最近一次裁决时间。Recap staleness 读时判脏的比较端（ADR-028 §6）。</summary>
        public DateTimeOffset UpdatedAt { get; set; }

        public Strand? Parent { get; set; }

        public List<StrandMatcher> Members { get; set; } = [];
    }
}
