using Heartbeat.Recording;

namespace Heartbeat.Application.Recording;

public sealed record CountPointRecordsQuery(
    Guid TrackId,
    DateTimeOffset From,
    DateTimeOffset To,
    int BucketSeconds);

public sealed record PointRecordCountBucket(
    int Index,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    long Count);

public sealed record PointRecordCounts(
    ReplayedTrack Track,
    DateTimeOffset From,
    DateTimeOffset To,
    int BucketSeconds,
    IReadOnlyList<PointRecordCountBucket> Buckets);

public abstract record CountPointRecordsResult
{
    private CountPointRecordsResult() { }

    public sealed record Found(PointRecordCounts Counts) : CountPointRecordsResult;
    public sealed record TrackNotFound : CountPointRecordsResult;
    public sealed record TrackIsNotPoint : CountPointRecordsResult;
}

public interface ICountPointRecords
{
    Task<CountPointRecordsResult> ExecuteAsync(
        Guid ownerId,
        CountPointRecordsQuery query,
        CancellationToken cancellationToken = default);
}

public interface IPointRecordCountStore
{
    Task<PointRecordCounts?> CountAsync(
        Guid ownerId,
        CountPointRecordsQuery query,
        CancellationToken cancellationToken = default);
}

public sealed class CountPointRecords(IPointRecordCountStore store) : ICountPointRecords
{
    public const int MaximumBuckets = 10_000;

    public async Task<CountPointRecordsResult> ExecuteAsync(
        Guid ownerId,
        CountPointRecordsQuery query,
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

        var from = query.From.ToUniversalTime();
        var to = query.To.ToUniversalTime();
        if (from >= to)
        {
            throw new ArgumentException("The count window must have a start before its end.", nameof(query));
        }
        if (query.BucketSeconds < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(query), "Bucket seconds must be positive.");
        }

        var bucketCount = Math.Ceiling((to - from).TotalSeconds / query.BucketSeconds);
        if (bucketCount > MaximumBuckets)
        {
            throw new ArgumentOutOfRangeException(nameof(query), $"A count window cannot exceed {MaximumBuckets} buckets.");
        }

        var normalized = query with { From = from, To = to };
        var counts = await store.CountAsync(ownerId, normalized, cancellationToken);
        if (counts is null)
        {
            return new CountPointRecordsResult.TrackNotFound();
        }
        return counts.Track.TimeMode == TimeMode.Point
            ? new CountPointRecordsResult.Found(counts)
            : new CountPointRecordsResult.TrackIsNotPoint();
    }
}
