using Heartbeat.Core.DTOs.Input;

namespace Heartbeat.Server.Entities
{
    /// <summary>
    /// Event 面向输入统计的 SQL 结果，不单独持久化。详见 ADR-012/055。
    /// </summary>
    public class InputEvent
    {
        /// <summary>家族事实的数据库行 Id；原生去重由 Owner/Stream/FactId 负责。</summary>
        public Guid Id { get; set; }

        public long DeviceId { get; set; }


        public InputEventType EventType { get; set; }

        /// <summary>Code 的原始解释版本；历史记录不会被投影结果回写。</summary>
        public string CodeSet { get; set; } = string.Empty;

        /// <summary>键盘=CodeSet 中的键位置；鼠标按钮=1左/2右/3中；滚轮=1上/2下。</summary>
        public short Code { get; set; }

        /// <summary>事件发生时刻（数据库微秒精度）。</summary>
        public DateTimeOffset Timestamp { get; set; }

        public Device Device { get; set; } = null!;
    }
}
