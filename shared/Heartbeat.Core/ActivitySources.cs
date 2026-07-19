namespace Heartbeat.Core
{
    /// <summary>
    /// ActivitySegment 的 Source 维度取值：一条段是"谁观测到的"。详见 ADR-017。
    /// </summary>
    public static class ActivitySources
    {
        /// <summary>
        /// 系统采集器（Agent 的前台窗口采集）。唯一观测前台性的 Source，
        /// 其段互斥、时长可求和——统计路径只消费此 Source。
        /// </summary>
        public const string System = "system";

        /// <summary>
        /// 浏览器扩展采集器。段为页面级活动，IdentityKey = 规范化 URL（origin+pathname）。
        /// </summary>
        public const string Browser = "browser";
    }
}
