using Heartbeat.Application.Recording;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Persistence;

internal sealed class PostgresRecordReplayStore(HeartbeatDbContext dbContext) : IRecordReplayStore
{
    public async Task<RecordReplay?> FindAsync(
        Guid ownerId,
        ReplayRecordsQuery query,
        CancellationToken cancellationToken = default)
    {
        var track = await (
            from candidate in dbContext.Tracks.AsNoTracking()
            join collector in dbContext.Collectors.AsNoTracking() on candidate.CollectorId equals collector.Id
            join timeline in dbContext.Timelines.AsNoTracking() on collector.TimelineId equals timeline.Id
            where candidate.Id == query.TrackId && timeline.OwnerId == ownerId
            select new ReplayedTrack(candidate.Id, candidate.CollectorId, candidate.Type,
                candidate.Version, candidate.TimeMode, candidate.EndMode))
            .SingleOrDefaultAsync(cancellationToken);

        if (track is null)
        {
            return null;
        }

        var records = dbContext.Records.AsNoTracking()
            .Where(record => record.TrackId == query.TrackId);

        if (query.From is not null)
        {
            var from = query.From.Value;
            records = records.Where(record =>
                record.EndedAt == null
                    ? record.StartedAt >= from
                    : record.EndedAt > from);
        }

        if (query.To is not null)
        {
            var to = query.To.Value;
            records = records.Where(record => record.StartedAt < to);
        }

        var stored = await records
            .OrderBy(record => record.StartedAt)
            .ThenBy(record => record.Id)
            .Take(query.Limit!.Value)
            .ToListAsync(cancellationToken);
        var replayed = stored
            .Select(record => new ReplayedRecord(
                record.Id,
                record.StartedAt,
                record.EndedAt,
                record.ObservedAt,
                record.ReceivedAt,
                record.Value.Clone()))
            .ToList();

        return new RecordReplay(track, replayed);
    }
}
