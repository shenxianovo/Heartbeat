using Heartbeat.Collection.Hub.Segments;
using Heartbeat.Core.DTOs.Segments;

namespace Heartbeat.Collection.Headless;

public sealed record HeadlessCurrentSubjectActivity(
    string? Title,
    string? IdentityKey,
    DateTimeOffset StartTime,
    DateTimeOffset EndTime,
    object? Attributes);

public sealed class HeadlessSubjectStatus
{
    private readonly object _gate = new();
    private HeadlessCurrentSubjectActivity? _current;
    private Guid? _currentSegmentId;

    public HeadlessCurrentSubjectActivity? Current
    {
        get { lock (_gate) return _current; }
    }

    internal void Observe(ActivitySegmentItem item, bool isFinal = false)
    {
        lock (_gate)
        {
            if (_current is not null && item.StartTime < _current.StartTime)
                return;
            if (isFinal)
            {
                if (_currentSegmentId == item.Id)
                {
                    _current = null;
                    _currentSegmentId = null;
                }
            }
            else
            {
                _current = new HeadlessCurrentSubjectActivity(
                    item.Title,
                    item.IdentityKey,
                    item.StartTime,
                    item.EndTime,
                    item.Attributes);
                _currentSegmentId = item.Id;
            }
        }
    }

    internal void Retract(Guid id)
    {
        lock (_gate)
        {
            if (_currentSegmentId == id)
            {
                _current = null;
                _currentSegmentId = null;
            }
        }
    }
}

internal sealed class HeadlessSubjectSegmentSink(
    SegmentIngestService inner,
    HeadlessSubjectStatus status) :
    ISegmentSink,
    ISegmentRetractionSink,
    IDurableSegmentProjectionSink,
    ISubjectSegmentProjectionSink,
    ICollectorTrafficSink
{
    public void Push(List<ActivitySegmentItem> snapshots)
    {
        inner.Push(snapshots);
        foreach (var snapshot in snapshots) status.Observe(snapshot);
    }

    public void Retract(Guid segmentId)
    {
        inner.Retract(segmentId);
        status.Retract(segmentId);
    }

    public void UpsertDurable(ActivitySegmentItem snapshot, long revision)
    {
        inner.UpsertDurable(snapshot, revision);
        status.Observe(snapshot);
    }

    public void ReplayDurable(ActivitySegmentItem snapshot, long revision)
    {
        inner.ReplayDurable(snapshot, revision);
        status.Observe(snapshot);
    }

    public void RetractDurable(Guid segmentId, long revision)
    {
        inner.RetractDurable(segmentId, revision);
        status.Retract(segmentId);
    }

    public void MarkSourceActive(string source) => inner.MarkSourceActive(source);

    public void UpsertDurable(
        CollectorProjectionContext context,
        ActivitySegmentItem snapshot,
        long revision,
        bool isFinal)
    {
        inner.UpsertDurable(snapshot, revision);
        status.Observe(snapshot, isFinal);
    }

    public void ReplayDurable(
        CollectorProjectionContext context,
        ActivitySegmentItem snapshot,
        long revision,
        bool isFinal)
    {
        inner.ReplayDurable(snapshot, revision);
        status.Observe(snapshot, isFinal);
    }

    public void RetractDurable(
        CollectorProjectionContext context,
        Guid segmentId,
        long revision)
    {
        inner.RetractDurable(segmentId, revision);
        status.Retract(segmentId);
    }
}
