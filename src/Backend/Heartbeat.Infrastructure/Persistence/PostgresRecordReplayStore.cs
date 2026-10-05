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
        var track = await ObjectQueries.FindTrackAsync(dbContext, ownerId, query.TrackId, cancellationToken);

        if (track is null)
        {
            return null;
        }

        var records = dbContext.Records.AsNoTracking()
            .Where(record => record.TrackId == query.TrackId)
            .ForObjects(dbContext, query.ObjectId, query.ContextObjectIds);

        records = records.InWindow(query.From, query.To, query.Cursor);

        var stored = await records
            .OrderBy(record => record.StartedAt)
            .ThenBy(record => record.Id)
            .Take(query.Limit!.Value + 1)
            .ToListAsync(cancellationToken);
        var hasNextPage = stored.Count > query.Limit.Value;
        if (hasNextPage)
        {
            stored.RemoveAt(stored.Count - 1);
        }

        var objects = await ObjectQueries.ReferencesAsync(dbContext, stored, cancellationToken);
        var replayed = stored
            .Select(record => new ReplayedRecord(
                record.Id,
                record.StartedAt,
                record.EndedAt,
                record.ObservedAt,
                record.ReceivedAt,
                record.Value.Clone()) { Objects = objects[record.Id] })
            .ToList();
        var nextCursor = hasNextPage
            ? new ReplayRecordsCursor(stored[^1].StartedAt, stored[^1].Id)
            : null;

        return new RecordReplay(track, replayed, nextCursor);
    }
}
