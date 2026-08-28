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
