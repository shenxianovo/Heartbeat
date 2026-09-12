namespace Heartbeat.Collector.Desktop.Mac;

public sealed record ForegroundRecord(
    Guid Id,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    ForegroundApplication Application);

public sealed class ForegroundRecordBatcher(TimeProvider timeProvider)
{
    private CurrentRecord? _current;
    private (DateTimeOffset UtcNow, long Timestamp)? _timeBase;

    public ForegroundRecord? Confirm(ForegroundApplication? application)
    {
        if (application is null)
        {
            _current = null;
            _timeBase = null;
            return null;
        }

        _timeBase ??= (timeProvider.GetUtcNow(), timeProvider.GetTimestamp());
        var observedAt = _timeBase.Value.UtcNow + timeProvider.GetElapsedTime(_timeBase.Value.Timestamp);
        if (_current is null || _current.Application != application)
        {
            _current = new CurrentRecord(Guid.CreateVersion7(observedAt), observedAt, application);
        }

        return new ForegroundRecord(_current.Id, _current.StartedAt, observedAt, application);
    }

    private sealed record CurrentRecord(
        Guid Id,
        DateTimeOffset StartedAt,
        ForegroundApplication Application);
}
