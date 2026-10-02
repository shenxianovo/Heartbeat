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
            records = records.Where(record => db.RecordObjects.Any(link => link.RecordId == record.Id && link.ObjectId == id));
        foreach (var context in contextObjectIds ?? [])
            records = records.Where(record => db.RecordObjects.Any(link => link.RecordId == record.Id && link.ObjectId == context));
        return records;
    }

    internal static async Task<Dictionary<Guid, IReadOnlyList<ReplayedObject>>> ReferencesAsync(
        HeartbeatDbContext db, IReadOnlyList<Record> records, CancellationToken cancellationToken)
    {
        var ids = records.Select(record => record.Id).ToArray();
        var links = await db.RecordObjects.AsNoTracking().Where(link => ids.Contains(link.RecordId))
            .Select(link => new { link.RecordId, link.ObjectId, link.Role, link.Subject.Namespace, link.Subject.Key })
            .ToListAsync(cancellationToken);
        var resolved = links.ToDictionary(link => (link.RecordId, link.Role, link.Namespace, link.Key), link => link.ObjectId);
        return records.ToDictionary(record => record.Id, record => (IReadOnlyList<ReplayedObject>)record.Objects
            .Select(item => new ReplayedObject(resolved[(record.Id, item.Role, item.Namespace, item.Key)],
                item.Role, item.Namespace, item.Key, item.Name)).ToArray());
    }
}
