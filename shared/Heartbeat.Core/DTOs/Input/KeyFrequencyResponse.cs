namespace Heartbeat.Core.DTOs.Input
{
    /// <summary>
    /// 键盘逐键按下次数（全部按键，不裁剪）。详见 ADR-012。
    /// </summary>
    public class KeyFrequencyResponse
    {
        public List<KeyFrequencyItem> Keys { get; set; } = [];
    }

    public class KeyFrequencyItem
    {
        /// <summary>heartbeat-key-position-v1 中的 canonical 物理键位置。</summary>
        public short Code { get; set; }

        public long Count { get; set; }
    }
}
