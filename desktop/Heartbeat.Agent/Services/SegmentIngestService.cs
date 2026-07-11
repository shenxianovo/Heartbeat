using Heartbeat.Core;
using Heartbeat.Core.DTOs.Segments;
using Heartbeat.Agent.Utils;
using Serilog;

namespace Heartbeat.Agent.Services
{
    /// <summary>
    /// 段的内存缓冲（ADR-017 枢纽的接收侧）。
    /// 接收 → 校验 → 缓冲，由 UploadWorker 周期性取走上传。
    /// 缓冲按 Id 键控（ADR-018）：同段后到快照覆盖先到——快照单调生长，
    /// 最新一份携带全部信息，攒批自动压缩。
    /// 同时维护集面读模型（ADR-021）：Current Activity + per-Source last-seen，
    /// 与缓冲分离，不随 drain 清空。
    /// </summary>
    public class SegmentIngestService(IClock clock) : ISegmentSink, IUploadSource<ActivitySegmentItem>, ICurrentActivitySink, ICollectionStatus
    {
        private readonly object _lock = new();
        private readonly Dictionary<Guid, ActivitySegmentItem> _segments = [];

        // ---- 集面读模型（ADR-021）：独立小锁，读写不与缓冲争用 ----
        private readonly object _statusLock = new();
        private string? _currentApp;
        private readonly Dictionary<string, DateTimeOffset> _lastSeen = [];

        /// <summary>缓冲上限：防失控采集器把 Agent 内存吃满（超出丢最旧）。</summary>
        private const int MaxBuffered = 20000;

        /// <summary>
        /// 接收一批段。返回接受的条数。source 无关（ADR-020）：冒充守卫在
        /// loopback 协议层（SegmentIngestRequestHandler），内置采集器进程内直调本方法。
        /// 缺 Id 的补 UUIDv7。
        /// </summary>
        public int Accept(List<ActivitySegmentItem> segments)
        {
            if (segments.Count == 0) return 0;

            foreach (var s in segments)
            {
                if (s.Id == Guid.Empty)
                    s.Id = Guid.CreateVersion7();
            }

            var valid = SegmentValidationPolicy.Filter(segments, clock.UtcNow);
            if (valid.Count == 0) return 0;

            lock (_lock)
            {
                foreach (var s in valid)
                {
                    if (_segments.Count >= MaxBuffered && !_segments.ContainsKey(s.Id))
                        EvictOldest();
                    _segments[s.Id] = s;
                }
            }

            // 读模型盖戳（ADR-021）：per-Source last-seen 从流量派生，即 Active 的机制。
            var now = clock.UtcNow;
            lock (_statusLock)
            {
                foreach (var src in valid.Select(v => v.Source).Distinct())
                    _lastSeen[src!] = now;
            }

            Log.Debug("接收段 {Count} 条（source: {Sources}）",
                valid.Count, string.Join(",", valid.Select(v => v.Source).Distinct()));
            return valid.Count;
        }

        /// <summary>ISegmentSink adapter（ADR-020）：内置采集器进程内推送，与 Accept 同一缓冲。</summary>
        public void Push(List<ActivitySegmentItem> snapshots) => Accept(snapshots);

        // ---- 集面读模型（ADR-021） ----

        public event Action<string?>? CurrentAppChanged;

        public string? CurrentApp
        {
            get { lock (_statusLock) return _currentApp; }
        }

        public IReadOnlyDictionary<string, DateTimeOffset> SourceLastSeen
        {
            get { lock (_statusLock) return new Dictionary<string, DateTimeOffset>(_lastSeen); }
        }

        /// <summary>
        /// ICurrentActivitySink adapter（ADR-021）：system 采集器在转场点推送。
        /// 值实际变化才广播，重复上报静默——下游（心跳的变了就推）依赖此去重。
        /// </summary>
        public void Report(string? app)
        {
            lock (_statusLock)
            {
                if (string.Equals(_currentApp, app, StringComparison.Ordinal))
                    return;
                _currentApp = app;
            }
            CurrentAppChanged?.Invoke(app);
        }

        /// <summary>IUploadSource adapter：出网侧的统一 drain 词汇。</summary>
        List<ActivitySegmentItem> IUploadSource<ActivitySegmentItem>.Drain() => GetAndClearSegments();

        /// <summary>
        /// IUploadSource adapter：退回批重注入（ADR-022）。缺席才插入，在位者保留
        /// EndTime 更晚的快照——批次在外期间 hub 可能已收到同 Id 的更新快照，
        /// 重注入不得回滚（与服务端单调生长门同一条规则，ADR-018）。
        /// 退回批已过一次门卫，不再校验——重过滤即丢数据，违背不蒸发不变量。
        /// </summary>
        void IUploadSource<ActivitySegmentItem>.Reinject(List<ActivitySegmentItem> items)
        {
            if (items.Count == 0) return;

            lock (_lock)
            {
                foreach (var s in items)
                {
                    if (_segments.TryGetValue(s.Id, out var existing) && existing.EndTime >= s.EndTime)
                        continue;
                    if (_segments.Count >= MaxBuffered && !_segments.ContainsKey(s.Id))
                        EvictOldest();
                    _segments[s.Id] = s;
                }
            }
            Log.Debug("重注入退回段 {Count} 条", items.Count);
        }

        public List<ActivitySegmentItem> GetAndClearSegments()
        {
            lock (_lock)
            {
                var copy = _segments.Values.OrderBy(s => s.StartTime).ToList();
                _segments.Clear();
                return copy;
            }
        }

        /// <summary>失效安全阀（调用方必须持有 _lock）：缓冲满时丢最旧段，留痕。</summary>
        private void EvictOldest()
        {
            var oldest = _segments.Values.MinBy(s => s.StartTime)!;
            _segments.Remove(oldest.Id);
            Log.Warning("段缓冲已满（{Max} 条），丢弃最旧段 {Id}（source: {Source}）",
                MaxBuffered, oldest.Id, oldest.Source);
        }
    }
}
