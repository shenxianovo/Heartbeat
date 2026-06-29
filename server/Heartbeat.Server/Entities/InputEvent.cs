using Heartbeat.Core.DTOs.Input;

namespace Heartbeat.Server.Entities
{
    /// <summary>
    /// 原始输入事件（一行一个键盘按下/鼠标操作）。详见 ADR-012。
    /// </summary>
    public class InputEvent
    {
        /// <summary>UUIDv7，由 Agent 生成，兼作主键与去重键。</summary>
        public Guid Id { get; set; }

        public long DeviceId { get; set; }

        public InputEventType EventType { get; set; }

        /// <summary>键盘=VK 码；鼠标按钮=1左/2右/3中；滚轮=1上/2下。</summary>
        public short Code { get; set; }

        /// <summary>事件发生时刻（毫秒精度，算速度的权威时间源）。</summary>
        public DateTimeOffset Timestamp { get; set; }

        public Device Device { get; set; } = null!;
    }
}
