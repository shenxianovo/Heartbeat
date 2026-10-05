using Heartbeat.Application.Recording;
using Heartbeat.Recording;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Persistence;

internal static class ObjectQueries
{
    internal static Task<ReplayedTrack?> FindTrackAsync(HeartbeatDbContext db, Guid ownerId, Guid trackId,
        CancellationToken cancellationToken) =>
        (from track in db.Tracks.AsNoTracking()
         join collector in db.Collectors on track.CollectorId equals collector.Id
         join timeline in db.Timelines on collector.TimelineId equals timeline.Id
         where track.Id == trackId && timeline.OwnerId == ownerId
         select new ReplayedTrack(track.Id, track.CollectorId, track.Type, track.Version, track.TimeMode))
        .SingleOrDefaultAsync(cancellationToken);

    internal static IQueryable<Record> InWindow(this IQueryable<Record> records,
        DateTimeOffset? from, DateTimeOffset? until, ReplayRecordsCursor? cursor)
    {
        if (from is { } start)
            records = records.Where(record => record.EndedAt == null || record.EndedAt == record.StartedAt
                ? record.StartedAt >= start : record.EndedAt > start);
        if (until is { } end) records = records.Where(record => record.StartedAt < end);
        if (cursor is not null) records = records.Where(record => record.StartedAt > cursor.StartedAt ||
            (record.StartedAt == cursor.StartedAt && record.Id.CompareTo(cursor.Id) > 0));
        return records;
    }

    internal static IQueryable<Record> ForObjects(this IQueryable<Record> records, HeartbeatDbContext db,
        Guid? objectId, IReadOnlyList<Guid>? contextObjectIds)
    {
        if (objectId is { } id)
            records = RelatedTo(records, db, id);
        foreach (var context in contextObjectIds ?? [])
            records = RelatedTo(records, db, context);
        return records;
    }

    private static IQueryable<Record> RelatedTo(IQueryable<Record> records, HeartbeatDbContext db, Guid id) =>
        records.Where(record => record.Id == id || record.TrackId == id ||
            db.Tracks.Any(track => track.Id == record.TrackId &&
                (track.CollectorId == id || db.Collectors.Any(collector => collector.Id == track.CollectorId && collector.TimelineId == id))) ||
            db.RecordObjects.Any(link => link.RecordId == record.Id && link.ObjectId == id));

    internal static async Task<Dictionary<Guid, IReadOnlyList<ReplayedObject>>> ReferencesAsync(
        HeartbeatDbContext db, IReadOnlyList<Record> records, CancellationToken cancellationToken)
    {
        var ids = records.Select(record => record.Id).ToArray();
        var links = await db.RecordObjects.AsNoTracking().Where(link => ids.Contains(link.RecordId))
            .Select(link => new { link.RecordId, link.ReferenceIndex, link.ObjectId }).ToListAsync(cancellationToken);
        var resolved = links.ToDictionary(link => (link.RecordId, link.ReferenceIndex), link => link.ObjectId);
        return records.ToDictionary(record => record.Id, record => (IReadOnlyList<ReplayedObject>)record.Objects
            .Select((item, index) => new ReplayedObject(resolved[(record.Id, index)],
                item.Role, item.Namespace, item.Key, item.Name) { Scope = item.Scope }).ToArray());
    }
}
