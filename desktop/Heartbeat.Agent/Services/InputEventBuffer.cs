using System.Collections.Concurrent;
using Heartbeat.Core.DTOs.Input;
using Heartbeat.Agent.Utils;

namespace Heartbeat.Agent.Services
{
    /// <summary>
    /// 输入事件的内存缓冲与归一化逻辑（不含 Win32 钩子，便于单测）。详见 ADR-012。
    ///
    /// 职责：
    /// - 过滤长按自动重复（同一键在 KeyUp 之前的重复 KeyDown 丢弃）
    /// - 滚轮碎 delta 累加归一为整档（±120 = 一档）
    /// - 入队封顶丢旧，防止常驻进程内存无界增长
    /// - 为每个事件生成 UUIDv7
    /// </summary>
    public sealed class InputEventBuffer(IClock clock, int capacity = 100_000)
    {
        public const int WheelDelta = 120;

        private readonly IClock _clock = clock;
        private readonly int _capacity = capacity;

        private readonly ConcurrentQueue<InputEventItem> _queue = new();
        private int _count;

        // 按住状态：记录当前处于按下状态的 VK 码，用于过滤自动重复
        private readonly HashSet<int> _heldKeys = [];
        private readonly object _heldLock = new();

        // 滚轮累计 delta（按方向分别累计余量）
        private int _scrollAccum;
        private readonly object _scrollLock = new();

        public int Count => Volatile.Read(ref _count);

        /// <summary>键盘按下。返回是否记录了事件（自动重复会被丢弃）。</summary>
        public bool OnKeyDown(int vkCode)
        {
            lock (_heldLock)
            {
                if (!_heldKeys.Add(vkCode))
                    return false; // 已按住 → 自动重复，丢弃
            }

            Enqueue(InputEventType.KeyDown, (short)vkCode);
            return true;
        }

        /// <summary>键盘抬起。仅解除按住状态，不落盘。</summary>
        public void OnKeyUp(int vkCode)
        {
            lock (_heldLock)
            {
                _heldKeys.Remove(vkCode);
            }
        }

        /// <summary>鼠标按钮按下。code: 1=左 2=右 3=中。</summary>
        public void OnMouseButton(short code)
        {
            Enqueue(InputEventType.MouseButton, code);
        }

        /// <summary>
        /// 滚轮原始 delta（来自 WM_MOUSEWHEEL，通常 ±120 的倍数，触摸板可能更碎）。
        /// 累加后每满一档（±120）记一个事件，余量保留。
        /// </summary>
        public void OnScroll(int rawDelta)
        {
            int notches;
            lock (_scrollLock)
            {
                _scrollAccum += rawDelta;
                notches = _scrollAccum / WheelDelta;
                _scrollAccum -= notches * WheelDelta;
            }

            if (notches == 0) return;

            // notches > 0 上滚(1)，< 0 下滚(2)
            short code = notches > 0 ? (short)1 : (short)2;
            int abs = Math.Abs(notches);
            for (int i = 0; i < abs; i++)
                Enqueue(InputEventType.MouseScroll, code);
        }

        /// <summary>取走当前所有事件并清空缓冲。</summary>
        public List<InputEventItem> DrainAll()
        {
            var result = new List<InputEventItem>();
            while (_queue.TryDequeue(out var item))
            {
                Interlocked.Decrement(ref _count);
                result.Add(item);
            }
            return result;
        }

        private void Enqueue(InputEventType type, short code)
        {
            var item = new InputEventItem
            {
                Id = Guid.CreateVersion7(),
                EventType = type,
                Code = code,
                Timestamp = _clock.UtcNow
            };

            _queue.Enqueue(item);
            var n = Interlocked.Increment(ref _count);

            // 封顶丢旧
            while (n > _capacity && _queue.TryDequeue(out _))
            {
                n = Interlocked.Decrement(ref _count);
            }
        }
    }
}
