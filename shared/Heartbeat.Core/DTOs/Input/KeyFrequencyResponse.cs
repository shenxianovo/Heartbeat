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
        /// <summary>Windows 虚拟键码（VK）。键名映射由展示层完成。</summary>
        public short Code { get; set; }

        public long Count { get; set; }
    }
}
