namespace Heartbeat.Observers.ForegroundState;

internal sealed record OutboxPeriod(long Sequence, Guid ObservationId, ForegroundCapture Capture);
