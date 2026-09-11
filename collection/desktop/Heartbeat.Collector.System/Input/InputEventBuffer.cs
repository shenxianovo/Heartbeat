using Heartbeat.Core.DTOs.Input;
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
    /// - 新观察经 System Collector Protocol → Runtime 原生 Fact journal 交付
    /// - legacy 缓冲仅排空升级前的 InputEvent；新观察容量由 ingress / Runtime 各自保管边界控制
    /// - 为每个事件生成 UUIDv7
    /// </summary>
    public sealed class InputEventBuffer : IUploadSource<InputEventItem>
    {
        public const int WheelDelta = 120;
        public const int UploadBatchSize = 5_000;
        public const string StatusStreamName = "输入事件 durable projection";

        private readonly IClock _clock;
        private readonly ISystemInputEventPublisher? _publisher;
        private const int DeliveryReceiptCapacity = 100_000;
        private readonly UploadStatusRegistry? _statusRegistry;
        private readonly JsonFileCache<InputEventItem>? _durableProjectionCache;
        private readonly JsonFileCache<Guid>? _deliveryReceiptCache;
        private readonly HashSet<Guid> _deliveredIds = [];
        private readonly object _durableGate = new();

        private int _count;

        // 按住状态：记录当前处于按下状态的物理键位置，用于过滤自动重复
        private readonly HashSet<short> _heldKeys = [];
        private readonly object _heldLock = new();

        // 滚轮累计 delta（按方向分别累计余量）
        private int _scrollAccum;
        private readonly object _scrollLock = new();

        public InputEventBuffer(
            IClock clock,
            ISystemInputEventPublisher? publisher = null,
            string? durableProjectionPath = null,
            UploadStatusRegistry? statusRegistry = null)
        {
            ArgumentNullException.ThrowIfNull(clock);
            _clock = clock;
            _publisher = publisher;
            _statusRegistry = statusRegistry;
            if (!string.IsNullOrWhiteSpace(durableProjectionPath))
            {
                _durableProjectionCache = new JsonFileCache<InputEventItem>(
                    durableProjectionPath,
                    int.MaxValue,
                    HeartbeatCacheFormats.InputEventVersion2(),
                    HeartbeatCacheFormats.InputEventMigrations());
                _count = _durableProjectionCache.Load().Count;

                var receiptPath = Path.Combine(
                    Path.GetDirectoryName(Path.GetFullPath(durableProjectionPath))!,
                    $"{Path.GetFileNameWithoutExtension(durableProjectionPath)}-delivery-receipts.json");
                var receiptCache = new JsonFileCache<Guid>(
                    receiptPath,
                    DeliveryReceiptCapacity,
                    HeartbeatCacheFormats.InputEventDeliveryReceiptVersion1());
                if (receiptCache.Status.State == CacheFileState.MigrationFailed)
                {
                    receiptCache.Dispose();
                }
                else
                {
                    _deliveryReceiptCache = receiptCache;
                    _deliveredIds.UnionWith(receiptCache.Load());
                }
            }
            UpdateStatus(_count);
        }

        public int Count => Volatile.Read(ref _count);
        DeliveryRemainder IUploadSource<InputEventItem>.Remainder => new(Count, 0);

        /// <summary>键盘按下。返回是否记录了事件（自动重复会被丢弃）。</summary>
        public bool OnKeyDown(InputKeyPosition position)
        {
            _ = Publisher;
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
            _ = Publisher;
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

        /// <summary>Read retained events without releasing custody.</summary>
        public List<InputEventItem> ReadAll()
        {
            lock (_durableGate)
                return _durableProjectionCache?.Load() ?? [];
        }

        public List<InputEventItem> ReadBatch()
        {
            lock (_durableGate)
                return (_durableProjectionCache?.Load() ?? []).Take(UploadBatchSize).ToList();
        }

        public void Confirm(IReadOnlyList<InputEventItem> items)
        {
            var confirmed = items.Select(item => item.Id).ToHashSet();
            lock (_durableGate)
            {
                var retained = (_durableProjectionCache?.Load() ?? [])
                    .Where(item => !confirmed.Contains(item.Id)).ToList();
                if (_durableProjectionCache is not null)
                    _durableProjectionCache.Replace(retained);
                Volatile.Write(ref _count, retained.Count);
                UpdateStatus(retained.Count);

                if (_deliveryReceiptCache is not null)
                {
                    var delivered = confirmed.Where(id => !_deliveredIds.Contains(id)).ToList();
                    if (delivered.Count > 0)
                    {
                        var receipts = _deliveryReceiptCache.Load();
                        receipts.AddRange(delivered);
                        _deliveryReceiptCache.Replace(receipts.TakeLast(DeliveryReceiptCapacity).ToList());
                        _deliveredIds.Clear();
                        _deliveredIds.UnionWith(_deliveryReceiptCache.Load());
                    }
                }
            }
        }

        private ISystemInputEventPublisher Publisher => _publisher ?? throw new InvalidOperationException(
            "New input requires a System Collector publisher; legacy caches only drain existing events.");

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
            Publisher.Publish(item);
        }

        private void UpdateStatus(int count)
        {
            if (_statusRegistry is null)
                return;
            var status = count switch
            {
                0 => UploadStreamStatus.Ready,
                _ => new UploadStreamStatus(
                    UploadStreamState.Backlog,
                    $"Retained legacy InputEvent backlog: {count}.")
            };
            _statusRegistry.Update(StatusStreamName, status);
        }
    }
}
