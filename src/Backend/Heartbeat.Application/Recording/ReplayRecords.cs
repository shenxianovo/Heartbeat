using System.Text.Json;
using Heartbeat.Recording;

namespace Heartbeat.Application.Recording;

public sealed record ReplayRecordsQuery(
    Guid TrackId,
    DateTimeOffset? From,
    DateTimeOffset? To,
    int? Limit);

public sealed record ReplayedTrack(
    Guid Id,
    Guid CollectorId,
    string Type,
    int Version,
    TimeMode TimeMode,
    EndMode? EndMode);

public sealed record ReplayedRecord(
    Guid Id,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    DateTimeOffset? ObservedAt,
    DateTimeOffset ReceivedAt,
    JsonElement Value);

public sealed record RecordReplay(ReplayedTrack Track, IReadOnlyList<ReplayedRecord> Records);

public abstract record ReplayRecordsResult
{
    private ReplayRecordsResult()
    {
    }

    public sealed record Found(RecordReplay Replay) : ReplayRecordsResult;

    public sealed record TrackNotFound : ReplayRecordsResult;
}

public interface IReplayRecords
{
    Task<ReplayRecordsResult> ExecuteAsync(
        Guid ownerId,
        ReplayRecordsQuery query,
        CancellationToken cancellationToken = default);
}

public interface IRecordReplayStore
{
    Task<RecordReplay?> FindAsync(
        Guid ownerId,
        ReplayRecordsQuery query,
        CancellationToken cancellationToken = default);
}

public sealed class ReplayRecords(IRecordReplayStore store) : IReplayRecords
{
    public const int DefaultLimit = 200;
    public const int MaximumLimit = 500;

    public async Task<ReplayRecordsResult> ExecuteAsync(
        Guid ownerId,
        ReplayRecordsQuery query,
        CancellationToken cancellationToken = default)
    {
        if (ownerId == Guid.Empty)
        {
            throw new ArgumentException("An owner is required.", nameof(ownerId));
        }

        ArgumentNullException.ThrowIfNull(query);
        if (query.TrackId == Guid.Empty)
        {
            throw new ArgumentException("A track is required.", nameof(query));
        }

        if (query.From is not null && query.To is not null && query.From >= query.To)
        {
            throw new ArgumentException("The replay window must have a start before its end.", nameof(query));
        }

        var limit = query.Limit ?? DefaultLimit;
        if (limit is < 1 or > MaximumLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(query), $"Limit must be between 1 and {MaximumLimit}.");
        }

        var normalized = query with
        {
            From = query.From?.ToUniversalTime(),
            To = query.To?.ToUniversalTime(),
            Limit = limit,
        };
        var replay = await store.FindAsync(ownerId, normalized, cancellationToken);

        return replay is null
            ? new ReplayRecordsResult.TrackNotFound()
            : new ReplayRecordsResult.Found(replay);
    }
}
