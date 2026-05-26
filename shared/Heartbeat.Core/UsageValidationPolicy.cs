using Heartbeat.Core.DTOs.Usage;

namespace Heartbeat.Core;

public static class UsageValidationPolicy
{
    public static readonly TimeSpan TimeSkewTolerance = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan MaxDuration = TimeSpan.FromHours(24);
    public static readonly int MinYear = 2020;

    public static List<AppUsageItem> Filter(List<AppUsageItem> usages, DateTimeOffset now)
    {
        return usages
            .Where(u => !string.IsNullOrEmpty(u.AppName)
                     && u.StartTime != default
                     && u.EndTime > u.StartTime
                     && u.StartTime.Year >= MinYear
                     && u.EndTime <= now + TimeSkewTolerance
                     && u.StartTime >= now - TimeSkewTolerance - MaxDuration
                     && (u.EndTime - u.StartTime) <= MaxDuration)
            .OrderBy(u => u.StartTime)
            .ToList();
    }
}
