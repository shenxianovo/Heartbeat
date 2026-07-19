namespace Heartbeat.Server.Entities
{
    /// <summary>
    /// 对一个锚点把手的负向裁决（ADR-028 §8）："不承载知识，别再问、别绑定"。
    /// 只作用于知识/提问层，不碰 Recap——被静音的把手照样如实进叙事。
    /// </summary>
    public class MutedHandle
    {
        public long Id { get; set; }

        public string OwnerId { get; set; } = string.Empty;

        public string Source { get; set; } = string.Empty;

        public string Token { get; set; } = string.Empty;

        public DateTimeOffset CreatedAt { get; set; }
    }
}
