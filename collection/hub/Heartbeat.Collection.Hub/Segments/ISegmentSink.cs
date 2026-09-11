using Heartbeat.Core.DTOs.Segments;
using Heartbeat.Collection.Hub.Collectors.Runtime;

namespace Heartbeat.Collection.Hub.Segments
{
    /// <summary>
    /// 段快照的接收侧 seam（ADR-020/040），供 Hub 组合层注入 Collector Runtime。
    /// 生产 adapter 是 <see cref="SegmentIngestService"/>；测试使用 fake 断言投影结果。
    /// </summary>
    public interface ISegmentSink
    {
        void Push(List<ActivitySegmentItem> snapshots);
    }

    /// <summary>
    /// Presence seam for validated live Collector protocol traffic. It is deliberately separate
    /// from projection so duplicate/superseded ACKs do not rebuffer an already drained Segment.
    /// </summary>
    public interface ICollectorTrafficSink
    {
        void MarkSourceActive(string source);
    }
}
