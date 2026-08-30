namespace Heartbeat.Collector.System.Collection;

/// <summary>
/// system Collector 对 foreground Segment Fact 的完整快照。Fact 身份与 Revision 由
/// Collector 持有；Transport Binding 只负责把这个语义快照编码到 Collector Protocol。
/// </summary>
public sealed record ForegroundSegmentSnapshot(
    Guid FactId,
    long Revision,
    string IdentityKey,
    string AppIdentityKey,
    string? AppDisplayName,
    string? Title,
    DateTimeOffset Start,
    DateTimeOffset End,
    bool IsFinal);

public interface ISystemSegmentPublisher
{
    void Publish(ForegroundSegmentSnapshot snapshot);

    void PublishBatch(IReadOnlyList<ForegroundSegmentSnapshot> snapshots)
    {
        foreach (var snapshot in snapshots)
            Publish(snapshot);
    }

    void StageDurableBatch(IReadOnlyList<ForegroundSegmentSnapshot> snapshots) =>
        PublishBatch(snapshots);

    void RecoverInterruptedSegment(DateTimeOffset recoveredAt)
    {
    }

    void ClearActiveCheckpoint(Guid factId, long revision)
    {
    }
}
