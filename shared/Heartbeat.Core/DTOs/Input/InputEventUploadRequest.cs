namespace Heartbeat.Core.DTOs.Input
{
    /// <summary>
    /// 输入事件类型。详见 ADR-012。
    /// </summary>
    public enum InputEventType : short
    {
        KeyDown = 1,
        MouseButton = 2,
        MouseScroll = 3,
    }

    public class InputEventUploadRequest
    {
        public List<InputEventItem> Events { get; set; } = [];
    }

    public class InputEventItem
    {
        /// <summary>客户端生成的 UUIDv7，兼作主键与去重键。</summary>
        public Guid Id { get; set; }

        public InputEventType EventType { get; set; }

        /// <summary>
        /// Code 的显式解释版本。新事件使用 heartbeat-key-position-v1；
        /// 历史 Windows 事件保持 windows-vk-v1，绝不猜测重写原始 Code。
        /// </summary>
        public string CodeSet { get; set; } = string.Empty;

        /// <summary>键盘=CodeSet 中的物理位置；鼠标按钮=1左/2右/3中；滚轮=1上/2下。</summary>
        public short Code { get; set; }

        public DateTimeOffset Timestamp { get; set; }
    }
}
