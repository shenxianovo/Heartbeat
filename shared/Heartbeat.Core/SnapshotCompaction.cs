using Heartbeat.Core.DTOs.Segments;
using Heartbeat.Core.DTOs.Usage;

namespace Heartbeat.Core
{
    /// <summary>
    /// 快照压缩（ADR-018）：同 Id 多快照只留最新（EndTime 最大）。
    /// 快照单调生长、最新快照携带全部信息，旧快照纯冗余——压缩只省流量，不影响正确性
    /// （服务端 upsert 对任意快照序列收敛到同一行）。
    /// </summary>
    public static class SnapshotCompaction
    {
        public static List<AppUsageItem> KeepLatest(List<AppUsageItem> items)
            => Compact(items, i => i.Id, i => i.EndTime, i => i.StartTime);

        public static List<ActivitySegmentItem> KeepLatest(List<ActivitySegmentItem> items)
            => Compact(items, i => i.Id, i => i.EndTime, i => i.StartTime);

        private static List<T> Compact<T>(
            List<T> items, Func<T, Guid> id, Func<T, DateTimeOffset> end, Func<T, DateTimeOffset> start)
        {
            if (items.Count <= 1) return items;
            // 旧版数据可能缺 Id（Guid.Empty）：不参与分组、原样透传，避免误并成一条。
            var legacy = items.Where(i => id(i) == Guid.Empty);
            return items
                .Where(i => id(i) != Guid.Empty)
                .GroupBy(id)
                .Select(g => g.MaxBy(end)!)
                .Concat(legacy)
                .OrderBy(start)
                .ToList();
        }
    }
}
