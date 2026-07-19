namespace Heartbeat.Server.Entities
{
    /// <summary>
    /// 一个把手的 LLM 分诊裁定缓存（ADR-028 §4 定稿）。分诊每把手一次 LLM 调用，
    /// 缓存使刷看板不重复烧 token：同把手判过就不再问 LLM，只透传缓存裁定。
    ///
    /// 无主动失效（沿 ADR-023 纪律）：裁决日志增长可能让旧裁定过时（few-shot 变了），
    /// 但不扇出重判——被绑定/Mute 的把手本就退出候选池，其余漂移可接受。
    /// Verdict 存字符串（known/ask/silent）以便 DB 可读。
    /// </summary>
    public class TriageDecision
    {
        public long Id { get; set; }

        public string OwnerId { get; set; } = string.Empty;

        public string Source { get; set; } = string.Empty;

        public string Token { get; set; } = string.Empty;

        /// <summary>known | ask | silent。</summary>
        public string Verdict { get; set; } = string.Empty;

        /// <summary>Known 时的世界知识名/释义；Ask 时的试探性提案（可空）。</summary>
        public string Name { get; set; } = string.Empty;

        public string Gloss { get; set; } = string.Empty;

        public DateTimeOffset DecidedAt { get; set; }
    }
}
