namespace Heartbeat.Server.Entities
{
    /// <summary>
    /// 对一个 Matcher 的负向裁决（ADR-029 §3/继承 ADR-028 §8）："不承载知识，别再问、别绑定"。
    /// 只作用于知识/发问层，不碰 Recap——被静音的观测照样如实进叙事。
    /// </summary>
    public class MutedMatcher
    {
        public long Id { get; set; }

        public string OwnerId { get; set; } = string.Empty;

        public string Source { get; set; } = string.Empty;

        /// <summary>规范化的 [{Layer, Reading, Op, Value}] JSON（MatcherCodec canonical）。</summary>
        public string StepsJson { get; set; } = string.Empty;

        public DateTimeOffset CreatedAt { get; set; }
    }
}
