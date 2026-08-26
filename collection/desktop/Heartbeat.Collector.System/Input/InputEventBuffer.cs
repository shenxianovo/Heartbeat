using System.Collections.Concurrent;
using Heartbeat.Core.DTOs.Input;
using Heartbeat.Collection.Hub.Collectors.Runtime;
using Heartbeat.Collection.Hub.Time;
using Heartbeat.Collection.Hub.Upload;
using Heartbeat.Collection.Hub.Storage;
using Heartbeat.Collector.System.Collection;

namespace Heartbeat.Collector.System.Input
{
    /// <summary>
    /// 输入事件的归一化与 legacy upload 缓冲（不含平台钩子，便于单测）。详见 ADR-012/041。
    ///
    /// 职责：
    /// - 过滤长按自动重复（同一键在 KeyUp 之前的重复 KeyDown 丢弃）
    /// - 滚轮碎 delta 累加归一为整档（±120 = 一档）
    /// - 生产观察经 system Collector Protocol 发布，Hub 提交后投影回 legacy upload 缓冲
    /// - 投影缓冲封顶丢旧，防止常驻进程内存无界增长
    /// - 为每个事件生成 UUIDv7
    /// </summary>
    public sealed class InputEventBuffer : IDurableUploadSource<InputEventItem>, IInputEventFactSink
    {
        public const int WheelDelta = 120;

        private readonly IClock _clock;
        private readonly ISystemInputEventPublisher? _publisher;
        private readonly int _capacity;
        private readonly JsonFileCache<InputEventItem>? _durableProjectionCache;
        private readonly object _durableGate = new();

        private readonly ConcurrentQueue<InputEventItem> _queue = new();
        private int _count;

        // 按住状态：记录当前处于按下状态的物理键位置，用于过滤自动重复
        private readonly HashSet<short> _heldKeys = [];
        private readonly object _heldLock = new();

        // 滚轮累计 delta（按方向分别累计余量）
        private int _scrollAccum;
        private readonly object _scrollLock = new();

        public InputEventBuffer(
            IClock clock,
            int capacity = 100_000,
            ISystemInputEventPublisher? publisher = null,
            string? durableProjectionPath = null)
        {
            ArgumentNullException.ThrowIfNull(clock);
            if (capacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(capacity));
            _clock = clock;
            _publisher = publisher;
            _capacity = capacity;
            if (!string.IsNullOrWhiteSpace(durableProjectionPath))
            {
                _durableProjectionCache = new JsonFileCache<InputEventItem>(
                    durableProjectionPath,
                    capacity,
                    HeartbeatCacheFormats.InputEventVersion2(),
                    HeartbeatCacheFormats.InputEventMigrations());
                _count = _durableProjectionCache.Load().Count;
            }
        }

        public int Count => Volatile.Read(ref _count);

        /// <summary>键盘按下。返回是否记录了事件（自动重复会被丢弃）。</summary>
        public bool OnKeyDown(InputKeyPosition position)
        {
            var code = (short)position;
            lock (_heldLock)
            {
                if (!_heldKeys.Add(code))
                    return false; // 已按住 → 自动重复，丢弃
            }

            Enqueue(InputEventType.KeyDown, code);
            return true;
        }

        /// <summary>键盘抬起。仅解除按住状态，不落盘。</summary>
        public void OnKeyUp(InputKeyPosition position)
        {
            lock (_heldLock)
            {
                _heldKeys.Remove((short)position);
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

        /// <summary>录制关闭时清空仅内存的 repeat / 精细滚轮状态，不触碰已生成事件。</summary>
        public void ResetTransientState()
        {
            lock (_heldLock)
            {
                _heldKeys.Clear();
            }

            lock (_scrollLock)
            {
                _scrollAccum = 0;
            }
        }

        /// <summary>
        /// 读取当前所有事件；内存测试模式会立即清空，生产持久模式等待 UploadStream 提交 drain 结果。
        /// </summary>
        public List<InputEventItem> DrainAll()
        {
            if (_durableProjectionCache is not null)
            {
                lock (_durableGate)
                    return _durableProjectionCache.Load();
            }
            var result = new List<InputEventItem>();
            while (_queue.TryDequeue(out var item))
            {
                Interlocked.Decrement(ref _count);
                result.Add(item);
            }
            return result;
        }

        /// <summary>
        /// 退回重注入（ADR-020 上传通道契约）：既没送达也没缓存住的批原样回队，
        /// 保留原 Id——服务端按 Id 幂等去重，重复注入不产生重复行。
        /// </summary>
        public void Requeue(List<InputEventItem> items)
        {
            foreach (var item in items)
                EnqueueItem(item);
        }

        /// <summary>IUploadSource adapter：出网侧的统一 drain 词汇。</summary>
        List<InputEventItem> IUploadSource<InputEventItem>.Drain() => DrainAll();

        /// <summary>IUploadSource adapter：退回批保 Id 回队。</summary>
        void IUploadSource<InputEventItem>.Reinject(List<InputEventItem> items) => Requeue(items);

        void IDurableUploadSource<InputEventItem>.CompleteDrain(
            IReadOnlyList<InputEventItem> drained,
            IReadOnlyList<InputEventItem> retryItems)
        {
            if (_durableProjectionCache is null)
            {
                Requeue(retryItems.ToList());
                return;
            }

            var drainedIds = drained.Select(item => item.Id).ToHashSet();
            var retryIds = retryItems.Select(item => item.Id).ToHashSet();
            lock (_durableGate)
            {
                var retained = _durableProjectionCache.Load()
                    .Where(item => !drainedIds.Contains(item.Id) || retryIds.Contains(item.Id))
                    .ToList();
                _durableProjectionCache.Replace(retained);
                Volatile.Write(ref _count, retained.Count);
            }
        }

        void IInputEventFactSink.Accept(InputEventItem item, bool isReplay) => EnqueueItem(item);

        private void Enqueue(InputEventType type, short code)
        {
            var item = new InputEventItem
            {
                Id = Guid.CreateVersion7(),
                EventType = type,
                CodeSet = InputCodeSets.HeartbeatKeyPositionV1,
                Code = code,
                Timestamp = _clock.UtcNow
            };
            if (_publisher is null)
                EnqueueItem(item);
            else
                _publisher.Publish(item);
        }

        private void EnqueueItem(InputEventItem item)
        {
            if (_durableProjectionCache is not null)
            {
                lock (_durableGate)
                {
                    var retained = _durableProjectionCache.Load();
                    if (retained.Any(existing => existing.Id == item.Id))
                        return;
                    retained.Add(item);
                    _durableProjectionCache.Replace(retained);
                    Volatile.Write(ref _count, Math.Min(retained.Count, _capacity));
                }
                return;
            }
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
