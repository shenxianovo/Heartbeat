namespace Heartbeat.Observers.ForegroundState;

internal sealed record OutboxPeriod(long Sequence, Guid ObservationId, EntitySubmission[] Entities, string? LastError);
