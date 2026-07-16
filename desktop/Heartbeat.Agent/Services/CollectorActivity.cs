using Heartbeat.Agent.Models;

namespace Heartbeat.Agent.Services
{
    /// <summary>
    /// Active（采集器活跃）判定（ADR-026 §3）：某 source 最近 &lt; 窗口 内推过段即活跃。
    /// 窗口从采集器自报的 flushPeriodMs 派生（3×，容一次丢失 flush + 一次重试），
    /// 未报时回落默认——不写死与采集器脱节的魔法常量。
    /// </summary>
    public static class CollectorActivity
    {
        /// <summary>窗口 = 此系数 × flushPeriodMs。3 容"一次丢失 flush + 一次 backoff 重试"。</summary>
        public const int WindowMultiplier = 3;

        /// <summary>采集器未报 flushPeriodMs 时的回落窗口（browser flush=30s 时 3× 即此值）。</summary>
        public static readonly TimeSpan DefaultWindow = TimeSpan.FromSeconds(90);

        public static TimeSpan Window(CollectorEntry? entry)
            => entry?.FlushPeriodMs is int ms && ms > 0
                ? TimeSpan.FromMilliseconds((long)ms * WindowMultiplier)
                : DefaultWindow;

        /// <summary>lastSeen 为 null（从未推过段）即不活跃。</summary>
        public static bool IsActive(DateTimeOffset? lastSeen, CollectorEntry? entry, DateTimeOffset now)
            => lastSeen is DateTimeOffset seen && now - seen < Window(entry);
    }
}
