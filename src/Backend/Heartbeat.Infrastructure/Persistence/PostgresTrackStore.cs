using Heartbeat.Application.Recording;
using Heartbeat.Persistence.Configurations;
using Heartbeat.Recording;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Persistence;

internal sealed class PostgresTrackStore(HeartbeatDbContext dbContext) : ITrackStore
{
    private static readonly TimeModeConverter TimeModeConverter = new();
    private static readonly EndModeConverter EndModeConverter = new();

    public Task<Track?> FindAsync(Guid ownerId, Guid trackId, CancellationToken cancellationToken = default) =>
        (from track in dbContext.Tracks.AsNoTracking()
         join collector in dbContext.Collectors on track.CollectorId equals collector.Id
         join timeline in dbContext.Timelines on collector.TimelineId equals timeline.Id
         where track.Id == trackId && timeline.OwnerId == ownerId
         select track).SingleOrDefaultAsync(cancellationToken);

    public async Task<ResolvedTrack?> ResolveAsync(
        Guid ownerId,
        Track candidate,
        CancellationToken cancellationToken = default)
    {
        var timeMode = (string)TimeModeConverter.ConvertToProvider(candidate.TimeMode)!;
        var endMode = (string?)EndModeConverter.ConvertToProvider(candidate.EndMode);

        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO tracks (id, collector_id, type, version, time_mode, end_mode, created_at)
            SELECT {candidate.Id}, collectors.id, {candidate.Type}, {candidate.Version},
                   {timeMode}, {endMode}, {candidate.CreatedAt}
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
                track.TimeMode, track.EndMode, track.CreatedAt))
            .SingleOrDefaultAsync(cancellationToken);
    }
}
