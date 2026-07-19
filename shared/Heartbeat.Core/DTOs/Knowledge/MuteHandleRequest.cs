namespace Heartbeat.Core.DTOs.Knowledge
{
    /// <summary>Mute 一个锚点把手（ADR-028 §8）：负向裁决，幂等。</summary>
    public class MuteHandleRequest
    {
        public string Source { get; set; } = string.Empty;

        public string Token { get; set; } = string.Empty;
    }
}
