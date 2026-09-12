using Heartbeat.Recording;

namespace Heartbeat.Application.Recording;

public interface IRecordStore
{
    Task<RecordWriteResult> WriteAsync(
        Guid ownerId,
        Record record,
        CancellationToken cancellationToken = default);
}

public abstract record RecordWriteResult
{
    private RecordWriteResult()
    {
    }

    public sealed record Stored(DateTimeOffset? EndedAt, DateTimeOffset ReceivedAt)
        : RecordWriteResult;

    public sealed record TrackNotFound : RecordWriteResult;

    public sealed record Conflict : RecordWriteResult;
}
