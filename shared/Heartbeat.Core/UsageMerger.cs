using Heartbeat.Core.DTOs.Usage;

namespace Heartbeat.Core
{
    /// <summary>
    /// 合并相邻同活动使用记录（处理上传截断产生的碎片）
    /// </summary>
    public static class UsageMerger
    {
        /// <summary>
        /// 合并容差：同活动首尾相连在此范围内的记录合并
        /// </summary>
        public static readonly TimeSpan MergeTolerance = TimeSpan.FromSeconds(1);

        /// <summary>
        /// 系统采集器（前台窗口）的 IdentityKey：规范化 AppName + Title。
        /// AppName 不区分大小写（沿用 ADR-015 前的判据），Title 区分；null 与空标题折叠
        /// （GetWindowText 对空标题返回 null，"" 实际不会出现）。
        /// 服务端 migration 的历史数据回填 SQL 与此定义必须一致。
        /// </summary>
        public static string SystemIdentityKey(string appName, string? title)
            => appName.ToLowerInvariant() + "\n" + (title ?? "");

        /// <summary>
        /// 两段是否可续接的判据（唯一真源，客户端与服务端共用）。
        /// 规则：同 Source + 同 IdentityKey（ordinal）且时间首尾相连/重叠（≤容差）。
        /// IdentityKey 由采集器声明"什么算同一个活动"，详见 ADR-017；
        /// system source 的 key 见 <see cref="SystemIdentityKey"/>（ADR-015 判据的泛化）。
        /// </summary>
        public static bool CanMerge(
            string prevSource, string prevIdentityKey, DateTimeOffset prevEnd,
            string currSource, string currIdentityKey, DateTimeOffset currStart)
        {
            return string.Equals(prevSource, currSource, StringComparison.Ordinal)
                && string.Equals(prevIdentityKey, currIdentityKey, StringComparison.Ordinal)
                && currStart <= prevEnd + MergeTolerance;
        }

        /// <summary>
        /// 将连续或重叠的同应用+同标题记录合并为一条（中间有其他应用/标题则不合并）。
        /// 输入为系统采集器批次（AppUsageItem 即 system source 的上传形状）。
        /// 不会修改传入的对象，返回全新的列表和对象。
        /// </summary>
        public static List<AppUsageItem> Merge(List<AppUsageItem> usages)
        {
            if (usages.Count <= 1) return usages;

            var sorted = usages.OrderBy(u => u.StartTime).ToList();

            var result = new List<AppUsageItem>
            {
                new() { Id = sorted[0].Id, AppName = sorted[0].AppName, Title = sorted[0].Title, StartTime = sorted[0].StartTime, EndTime = sorted[0].EndTime }
            };

            for (var i = 1; i < sorted.Count; i++)
            {
                var prev = result[^1];
                var curr = sorted[i];

                if (CanMerge(
                    ActivitySources.System, SystemIdentityKey(prev.AppName, prev.Title), prev.EndTime,
                    ActivitySources.System, SystemIdentityKey(curr.AppName, curr.Title), curr.StartTime))
                {
                    // 同活动且重叠或首尾相连 → 扩展上一条的结束时间（保留最早段的 Id，保证重传幂等）
                    if (curr.EndTime > prev.EndTime)
                        prev.EndTime = curr.EndTime;
                }
                else
                {
                    result.Add(new() { Id = curr.Id, AppName = curr.AppName, Title = curr.Title, StartTime = curr.StartTime, EndTime = curr.EndTime });
                }
            }

            return result;
        }
    }
}
