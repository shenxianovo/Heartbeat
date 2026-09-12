using Heartbeat.Recording;

namespace Heartbeat.Application.Recording;

// Protocol-specific upload code calls this only for continuous state, not arbitrary ranges.
public interface IContinuousStateStore
{
    Task<ContinuousStateWriteResult> WriteAsync(
        Guid ownerId,
        Record record,
        CancellationToken cancellationToken = default);
}

public abstract record ContinuousStateWriteResult
{
    private ContinuousStateWriteResult()
    {
    }

    public sealed record Stored(DateTimeOffset EndedAt, DateTimeOffset ReceivedAt)
        : ContinuousStateWriteResult;

    public sealed record TrackNotFound : ContinuousStateWriteResult;

    public sealed record Conflict : ContinuousStateWriteResult;
}
