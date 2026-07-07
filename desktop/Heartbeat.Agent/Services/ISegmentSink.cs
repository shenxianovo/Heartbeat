using Heartbeat.Core.DTOs.Segments;

namespace Heartbeat.Agent.Services
{
    /// <summary>
    /// 段快照的接收侧 seam（ADR-020）：内置 system 采集器（AppMonitorService）
    /// 把快照推进 hub 缓冲，与插件采集器经 loopback 推送同构。
    /// 生产 adapter 是 <see cref="SegmentIngestService"/>；测试用 fake 断言推出的段。
    /// </summary>
    public interface ISegmentSink
    {
        void Push(List<ActivitySegmentItem> snapshots);
    }
}
