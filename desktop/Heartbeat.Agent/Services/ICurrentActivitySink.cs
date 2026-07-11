namespace Heartbeat.Agent.Services
{
    /// <summary>
    /// Current Activity 的写入口（ADR-021）：system 采集器在转场点
    /// （初始前台、前台切换、进出 away）推送当前活动，hub 是唯一生产 adapter。
    /// 与 ISegmentSink 同构的单方法 seam。
    /// </summary>
    public interface ICurrentActivitySink
    {
        /// <summary>上报当前活动。away 原样上报（SyntheticApps.Away），无前台为 null。</summary>
        void Report(string? app);
    }
}
