using Heartbeat.Core.DTOs;

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
        /// 将连续的同应用记录合并为一条（仅合并时间上严格相邻的记录，中间有其他应用则不合并）
        /// </summary>
        public static List<AppUsageItem> Merge(List<AppUsageItem> usages)
        {
            if (usages.Count <= 1) return usages;

            var sorted = usages.OrderBy(u => u.StartTime).ToList();
            var result = new List<AppUsageItem> { sorted[0] };

            for (var i = 1; i < sorted.Count; i++)
            {
                var prev = result[^1];
                var curr = sorted[i];

                if (string.Equals(prev.AppName, curr.AppName, StringComparison.OrdinalIgnoreCase)
                    && curr.StartTime >= prev.EndTime
                    && curr.StartTime <= prev.EndTime + MergeTolerance)
                {
                    // 同应用且首尾相连 → 扩展上一条的结束时间
                    if (curr.EndTime > prev.EndTime)
                        prev.EndTime = curr.EndTime;
                }
                else
                {
                    result.Add(curr);
                }
            }

            return result;
        }
    }
}
