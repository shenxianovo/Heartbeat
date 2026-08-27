using Heartbeat.Core.DTOs.Segments;
using Heartbeat.Collection.Hub.Collectors.Runtime;

namespace Heartbeat.Collection.Hub.Segments
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

    /// <summary>
    /// Optional projection capability used by the Collector Fact runtime when a durable Segment
    /// revision retracts a snapshot that may still be present in the legacy Hub buffer.
    /// </summary>
    public interface ISegmentRetractionSink
    {
        void Retract(Guid segmentId);
    }

    /// <summary>
    /// Projection seam for Collector Facts that already passed their versioned schema and were
    /// durably accepted. Unlike transient legacy ingest, replayed/offline Facts must not be
    /// discarded by a wall-clock freshness filter.
    /// </summary>
    public interface IDurableSegmentProjectionSink
    {
        /// <summary>Projects one current durable Fact revision without applying legacy freshness rules.</summary>
        void UpsertDurable(ActivitySegmentItem snapshot, long revision);

        /// <summary>Restores a durable Fact without making historical replay look like live traffic.</summary>
        void ReplayDurable(ActivitySegmentItem snapshot, long revision);

        /// <summary>Applies a durable tombstone at the supplied Fact revision.</summary>
        void RetractDurable(Guid segmentId, long revision);
    }

    /// <summary>
    /// Subject-aware durable projection used by a multi-Subject Hub head. Finality and ownership
    /// stay in the Collection context instead of leaking into the strict Analytics DTO.
    /// </summary>
    public readonly record struct CollectorProjectionContext(
        Guid CollectorInstanceId,
        SubjectReference Subject);

    public interface ISubjectSegmentProjectionSink
    {
        void UpsertDurable(
            CollectorProjectionContext context,
            ActivitySegmentItem snapshot,
            long revision,
            bool isFinal);

        void ReplayDurable(
            CollectorProjectionContext context,
            ActivitySegmentItem snapshot,
            long revision,
            bool isFinal);

        void RetractDurable(CollectorProjectionContext context, Guid segmentId, long revision);
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
