using Heartbeat.Application.Recording;
using Heartbeat.Persistence.Configurations;
using Heartbeat.Recording;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Persistence;

internal sealed class PostgresTrackStore(HeartbeatDbContext dbContext) : ITrackStore
{
    private static readonly TimeModeConverter TimeModeConverter = new();

    public Task<Track?> FindAsync(Guid ownerId, Guid trackId, CancellationToken cancellationToken = default) =>
        (from track in dbContext.Tracks.AsNoTracking()
         join collector in dbContext.Collectors on track.CollectorId equals collector.Id
         join timeline in dbContext.Timelines on collector.TimelineId equals timeline.Id
         where track.Id == trackId && timeline.OwnerId == ownerId
         select track).SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<ListedTrack>> ListAsync(
        Guid ownerId,
        Guid? objectId = null, IReadOnlyList<Guid>? contextObjectIds = null, CancellationToken cancellationToken = default)
    {
        var records = dbContext.Records.ForObjects(dbContext, objectId, contextObjectIds);
        return await (
            from track in dbContext.Tracks.AsNoTracking()
            join collector in dbContext.Collectors.AsNoTracking() on track.CollectorId equals collector.Id
            join timeline in dbContext.Timelines.AsNoTracking() on collector.TimelineId equals timeline.Id
            where timeline.OwnerId == ownerId
            where (objectId == null && (contextObjectIds == null || contextObjectIds.Count == 0)) || records.Any(record => record.TrackId == track.Id)
            orderby collector.Key, collector.Target, track.Type, track.Version, track.Id
            select new ListedTrack(
                track.Id,
                collector.Id,
                collector.Key,
                collector.Target,
                collector.DisplayName,
                track.Type,
                track.Version,
                track.TimeMode,
                track.CreatedAt))
        .ToListAsync(cancellationToken);
    }

    public async Task<ResolvedTrack?> ResolveAsync(
        Guid ownerId,
        Track candidate,
        CancellationToken cancellationToken = default)
    {
        var timeMode = (string)TimeModeConverter.ConvertToProvider(candidate.TimeMode)!;

        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO tracks (id, collector_id, type, version, time_mode, created_at)
            SELECT {candidate.Id}, collectors.id, {candidate.Type}, {candidate.Version},
                   {timeMode}, {candidate.CreatedAt}
            FROM collectors
            JOIN timelines ON timelines.id = collectors.timeline_id
            WHERE collectors.id = {candidate.CollectorId} AND timelines.owner_id = {ownerId}
            ON CONFLICT (collector_id, type, version) DO NOTHING;
            """, cancellationToken);

        // A separate read also sees a concurrent insertion after ON CONFLICT has waited for it.
        return await (
            from track in dbContext.Tracks.AsNoTracking()
            join collector in dbContext.Collectors on track.CollectorId equals collector.Id
            join timeline in dbContext.Timelines on collector.TimelineId equals timeline.Id
            where timeline.OwnerId == ownerId
                && track.CollectorId == candidate.CollectorId
                && track.Type == candidate.Type
                && track.Version == candidate.Version
            select new ResolvedTrack(track.Id, track.CollectorId, track.Type, track.Version,
                track.TimeMode, track.CreatedAt))
            .SingleOrDefaultAsync(cancellationToken);
    }
}
