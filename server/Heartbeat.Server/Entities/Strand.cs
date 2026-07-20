namespace Heartbeat.Server.Entities
{
    /// <summary>
    /// 知识层核心对象（ADR-028 §2，指纹单位随 ADR-029 换 Matcher）：用户生活里一个有名字的持续活动线索，
    /// 形状 = 名字 + 自由释义 + 指纹（一组 Matcher）。
    /// 策展层，非派生物——库里只存用户亲口确认的事实（ADR-029 §1）；绝不写回 segment。
    /// </summary>
    public class Strand
    {
        /// <summary>UUIDv7，服务端生成。</summary>
        public Guid Id { get; set; }

        public string OwnerId { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        /// <summary>自由释义：这个东西在用户自己的话里是什么。绝不加 schema——通用工具、共现模式都写在这里（ADR-029 §5）。</summary>
        public string Gloss { get; set; } = string.Empty;

        public DateTimeOffset CreatedAt { get; set; }

        /// <summary>名字/释义/成员最近一次裁决时间。Recap staleness 读时判脏的比较端（ADR-028 §6）。</summary>
        public DateTimeOffset UpdatedAt { get; set; }

        public List<StrandMatcher> Members { get; set; } = [];
    }
}
