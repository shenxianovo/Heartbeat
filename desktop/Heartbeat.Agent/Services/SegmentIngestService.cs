using Heartbeat.Core;
using Heartbeat.Core.DTOs.Segments;
using Heartbeat.Agent.Utils;
using Serilog;

namespace Heartbeat.Agent.Services
{
    /// <summary>
    /// 插件段的内存缓冲（ADR-017 枢纽的接收侧）。
    /// 接收 → 校验 → 缓冲，由 UsageUploadWorker 周期性取走上传。
    /// 缓冲按 Id 键控（ADR-018）：同段后到快照覆盖先到——快照单调生长，
    /// 最新一份携带全部信息，攒批自动压缩。
    /// </summary>
    public class SegmentIngestService(IClock clock)
    {
        private readonly object _lock = new();
        private readonly Dictionary<Guid, ActivitySegmentItem> _segments = [];

        /// <summary>缓冲上限：防失控采集器把 Agent 内存吃满（超出丢最旧）。</summary>
        private const int MaxBuffered = 20000;

        /// <summary>
        /// 接收一批插件段。返回接受的条数。
        /// 拒绝 'system'（保留给内置采集器，防冒充污染统计互斥轨）；缺 Id 的补 UUIDv7。
        /// </summary>
        public int Accept(List<ActivitySegmentItem> segments)
        {
            if (segments.Count == 0) return 0;

            foreach (var s in segments)
            {
                if (string.Equals(s.Source, ActivitySources.System, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidSourceException($"Source '{ActivitySources.System}' is reserved for the built-in collector.");

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

            Log.Debug("接收插件段 {Count} 条（source: {Sources}）",
                valid.Count, string.Join(",", valid.Select(v => v.Source).Distinct()));
            return valid.Count;
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
            Log.Warning("插件段缓冲已满（{Max} 条），丢弃最旧段 {Id}（source: {Source}）",
                MaxBuffered, oldest.Id, oldest.Source);
        }
    }

    /// <summary>插件段 source 非法（如冒充 'system'）。</summary>
    public class InvalidSourceException(string message) : Exception(message);
}
