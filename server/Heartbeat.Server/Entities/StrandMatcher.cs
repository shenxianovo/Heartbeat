namespace Heartbeat.Server.Entities
{
    /// <summary>
    /// Strand 指纹的一个 Matcher（ADR-029 §3）：沿 Source 深度树的路径谓词，按值存。
    /// StepsJson 为规范化步骤序列（MatcherCodec canonical 序列化）——幂等比较按字符串相等。
    /// UUID 只是持久化身份（ADR-031 §1）；业务等价性仍由 (Source, canonical StepsJson) 决定。
    /// Anchor/Satellite 是策展纪律不落库（ADR-029 §5）。
    /// </summary>
    public class StrandMatcher
    {
        /// <summary>UUIDv7，应用层生成。</summary>
        public Guid Id { get; set; }

        public Guid StrandId { get; set; }

        public string Source { get; set; } = string.Empty;

        /// <summary>规范化的 [{Reading, Op, Value}] JSON。</summary>
        public string StepsJson { get; set; } = string.Empty;

        public Strand Strand { get; set; } = null!;
    }
}
