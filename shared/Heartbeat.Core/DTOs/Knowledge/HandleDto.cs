namespace Heartbeat.Core.DTOs.Knowledge
{
    /// <summary>知识层的可观测身份单元（ADR-028 §3）：Token 为该 Source 的粗身份（browser→domain、system→AppName）。</summary>
    public class HandleDto
    {
        public string Source { get; set; } = string.Empty;

        public string Token { get; set; } = string.Empty;
    }
}
