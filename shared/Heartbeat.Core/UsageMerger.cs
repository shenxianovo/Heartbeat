using Heartbeat.Core.DTOs.Usage;

namespace Heartbeat.Core
{
    /// <summary>
    /// 合并相邻同应用使用记录（处理上传截断产生的碎片）
    /// </summary>
    public static class UsageMerger
    {
        /// <summary>
        /// 合并容差：同应用首尾相连在此范围内的记录合并
        /// </summary>
        public static readonly TimeSpan MergeTolerance = TimeSpan.FromSeconds(1);

        /// <summary>
        /// 将连续或重叠的同应用记录合并为一条（中间有其他应用则不合并）。
        /// 不会修改传入的对象，返回全新的列表和对象。
        /// </summary>
        public static List<AppUsageItem> Merge(List<AppUsageItem> usages)
        {
            if (usages.Count <= 1) return usages;

            var sorted = usages.OrderBy(u => u.StartTime).ToList();

            // 克隆首条，避免修改原始对象（LocalCache 共享引用）
            var result = new List<AppUsageItem>
            {
                new() { AppName = sorted[0].AppName, StartTime = sorted[0].StartTime, EndTime = sorted[0].EndTime }
            };

            for (var i = 1; i < sorted.Count; i++)
            {
                var prev = result[^1];
                var curr = sorted[i];

                if (string.Equals(prev.AppName, curr.AppName, StringComparison.OrdinalIgnoreCase)
                    && curr.StartTime <= prev.EndTime + MergeTolerance)
                {
                    // 同应用且重叠或首尾相连 → 扩展上一条的结束时间
                    if (curr.EndTime > prev.EndTime)
                        prev.EndTime = curr.EndTime;
                }
                else
                {
                    result.Add(new() { AppName = curr.AppName, StartTime = curr.StartTime, EndTime = curr.EndTime });
                }
            }

            return result;
        }
    }
}
