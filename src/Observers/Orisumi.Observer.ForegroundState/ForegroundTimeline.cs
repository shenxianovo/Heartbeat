using Orisumi.Core;

namespace Orisumi.Observers.ForegroundState;

internal sealed class ForegroundTimeline(TimeSpan maximumGap)
{
    private ForegroundCapture? _current;
    private ForegroundApplicationReading? _previous;

    internal IReadOnlyList<ForegroundCapture> Accept(LocalApplication application, ForegroundApplicationReading reading)
    {
        var updates = new List<ForegroundCapture>();
        var continuous = _previous is not null && IsContinuous(_previous, reading);
        if (_current is not null && continuous)
        {
            var extended = _current.Through(reading.ObservedAt);
            if (_current.Application.Id == application.Id && _current.Content.Name == reading.Name
                && _current.TimeZone == reading.TimeZone)
            {
                _current = extended;
                _previous = reading;
                return [extended];
            }
            updates.Add(extended);
        }
        if (_current is not null && !continuous) updates.Add(_current);
        _current = ForegroundCapture.Create(application, reading);
        _previous = reading;
        updates.Add(_current);
        return updates;
    }

    internal void Interrupt()
    {
        // Each accepted sample already saved its confirmed end; never extend through a gap.
        _current = null;
        _previous = null;
    }

    private bool IsContinuous(ForegroundApplicationReading before, ForegroundApplicationReading after)
    {
        var wall = after.ObservedAt - before.ObservedAt;
        if (wall <= TimeSpan.Zero || wall > maximumGap) return false;
        if (before.Elapsed is not { } start || after.Elapsed is not { } end) return true;
        var elapsed = end - start;
        if (elapsed <= TimeSpan.Zero || elapsed > maximumGap
            || (wall - elapsed).Duration() > TimeSpan.FromMilliseconds(100)) return false;
        return before.Awake is not { } awakeStart || after.Awake is not { } awakeEnd
            || (elapsed - (awakeEnd - awakeStart)).Duration() <= TimeSpan.FromMilliseconds(100);
    }
}
