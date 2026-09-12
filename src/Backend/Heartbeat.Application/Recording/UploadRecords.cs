using System.Text.Json;
using RecordingRecord = Heartbeat.Recording.Record;

namespace Heartbeat.Application.Recording;

public sealed record RecordUpload(
    Guid Id,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt,
    DateTimeOffset? ObservedAt,
    JsonElement Value);

public sealed record UploadRecordsCommand(Guid TrackId, IReadOnlyList<RecordUpload?>? Records);

public enum RecordUploadStatus
{
    Stored,
    InvalidRecord,
    Conflict,
    TrackNotFound,
}

public sealed record RecordUploadResult(
    int Index,
    Guid? Id,
    RecordUploadStatus Status,
    DateTimeOffset? EndedAt = null,
    DateTimeOffset? ReceivedAt = null,
    string? Detail = null);

public abstract record UploadRecordsResult
{
    private UploadRecordsResult()
    {
    }

    public sealed record Completed(IReadOnlyList<RecordUploadResult> Results) : UploadRecordsResult;

    public sealed record TrackNotFound : UploadRecordsResult;

}

public interface IUploadRecords
{
    Task<UploadRecordsResult> ExecuteAsync(
        Guid ownerId, UploadRecordsCommand command, CancellationToken cancellationToken = default);
}

public sealed class UploadRecords(
    ITrackStore tracks,
    IRecordStore store,
    TimeProvider timeProvider) : IUploadRecords
{
    public const int MaximumBatchSize = 500;

    public async Task<UploadRecordsResult> ExecuteAsync(
        Guid ownerId, UploadRecordsCommand command, CancellationToken cancellationToken = default)
    {
        if (ownerId == Guid.Empty)
        {
            throw new ArgumentException("An owner is required.", nameof(ownerId));
        }

        ArgumentNullException.ThrowIfNull(command);
        if (command.TrackId == Guid.Empty)
        {
            throw new ArgumentException("A track is required.", nameof(command));
        }

        if (command.Records is null || command.Records.Count is < 1 or > MaximumBatchSize)
        {
            throw new ArgumentException($"A batch must contain between 1 and {MaximumBatchSize} records.", nameof(command));
        }

        var track = await tracks.FindAsync(ownerId, command.TrackId, cancellationToken);
        if (track is null)
        {
            return new UploadRecordsResult.TrackNotFound();
        }

        var receivedAt = timeProvider.GetUtcNow();
        var results = new List<RecordUploadResult>(command.Records.Count);
        for (var index = 0; index < command.Records.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var upload = command.Records[index];
            RecordingRecord record;
            try
            {
                if (upload?.StartedAt is null)
                {
                    throw new ArgumentException("A record with a start time is required.", nameof(command));
                }

                record = RecordingRecord.Create(upload.Id, track, upload.StartedAt.Value,
                    upload.EndedAt, upload.ObservedAt, receivedAt, upload.Value);
            }
            catch (ArgumentException exception)
            {
                results.Add(new RecordUploadResult(index, upload?.Id,
                    RecordUploadStatus.InvalidRecord, Detail: exception.Message));
                continue;
            }

            // Each store call commits independently; infrastructure failures propagate so retries keep the same IDs.
            var written = await store.WriteAsync(ownerId, record, cancellationToken);
            results.Add(written switch
            {
                RecordWriteResult.Stored stored => new RecordUploadResult(index, record.Id,
                    RecordUploadStatus.Stored, stored.EndedAt, stored.ReceivedAt),
                RecordWriteResult.Conflict => new RecordUploadResult(index, record.Id, RecordUploadStatus.Conflict),
                RecordWriteResult.TrackNotFound => new RecordUploadResult(index, record.Id, RecordUploadStatus.TrackNotFound),
                _ => throw new InvalidOperationException("Unknown record write result."),
            });
        }

        return new UploadRecordsResult.Completed(results);
    }
}
