namespace Heartbeat.Hub;

public sealed record DeliveryRoute(
    long Id,
    CollectorDeclaration Collector,
    TrackDeclaration Track,
    Guid? BackendCollectorId,
    Guid? BackendTrackId);

public sealed record PendingRecord(DeliveryRoute Route, RecordSnapshot Record, string? Failure = null);

public sealed record DeliveryOutcome(PendingRecord Sent, bool Stored, string? Failure = null);

public sealed record QueueStatus(long Pending, long Failed);

public sealed class RecordConflictException(string? message = null)
    : Exception(message ?? "The Record ID already has different fixed fields or ownership.");

public sealed class QueueCapacityException() : Exception("The Hub queue is full; custody was not accepted.");
