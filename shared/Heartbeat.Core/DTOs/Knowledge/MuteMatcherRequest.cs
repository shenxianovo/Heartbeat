namespace Heartbeat.Core.DTOs.Knowledge
{
    /// <summary>Mute 一个 Matcher（ADR-029 §3/继承 ADR-028 §8）：负向裁决，幂等。</summary>
    public class MuteMatcherRequest
    {
        public MatcherDto Matcher { get; set; } = new();
    }
}
