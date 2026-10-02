using Heartbeat.Application.Recording;
using Heartbeat.Recording;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Persistence;

internal sealed class PostgresObjectStore(HeartbeatDbContext db) : IObjectStore
{
    private IQueryable<ObservedObject> Owned(Guid ownerId) =>
        from item in db.Objects.AsNoTracking()
        join timeline in db.Timelines on item.TimelineId equals timeline.Id
        where timeline.OwnerId == ownerId
        select item;

    private IQueryable<ListedObject> Describe(IQueryable<ObservedObject> objects) =>
        objects.Select(item => new ListedObject(item.Id, item.Namespace, item.Key, item.Name,
            db.RecordObjects.Where(link => link.ObjectId == item.Id).Select(link => link.Role).Distinct().OrderBy(role => role).ToArray()));

    public async Task<IReadOnlyList<ListedObject>> ListAsync(Guid ownerId, IReadOnlyList<Guid>? contextObjectIds,
        Guid? objectId = null, CancellationToken cancellationToken = default)
    {
        var objects = Owned(ownerId);
        if (contextObjectIds is not null || objectId is not null)
        {
            var records = db.Records.ForObjects(db, objectId, contextObjectIds);
            objects = objects.Where(item => db.RecordObjects.Any(link => link.ObjectId == item.Id &&
                records.Any(record => record.Id == link.RecordId)));
        }
        return await Describe(objects.OrderBy(item => item.Namespace).ThenBy(item => item.Key)).ToListAsync(cancellationToken);
    }

    public Task<ListedObject?> FindAsync(Guid ownerId, Guid objectId, CancellationToken cancellationToken = default) =>
        Describe(Owned(ownerId).Where(item => item.Id == objectId)).SingleOrDefaultAsync(cancellationToken);

    public async Task<ObjectReplay> ReplayAsync(Guid ownerId, Guid objectId, IReadOnlyList<Guid>? contextObjectIds,
        DateTimeOffset? from, DateTimeOffset? until, int limit, ReplayRecordsCursor? cursor,
        CancellationToken cancellationToken = default)
    {
        var ownedRecords = from record in db.Records.AsNoTracking()
            join track in db.Tracks on record.TrackId equals track.Id
            join collector in db.Collectors on track.CollectorId equals collector.Id
            join timeline in db.Timelines on collector.TimelineId equals timeline.Id
            where timeline.OwnerId == ownerId select record;
        var query = ownedRecords.ForObjects(db, objectId, contextObjectIds);
        query = query.InWindow(from, until, cursor);
        var records = await query.OrderBy(record => record.StartedAt).ThenBy(record => record.Id)
            .Take(limit + 1).ToListAsync(cancellationToken);
        var more = records.Count > limit;
        if (more) records.RemoveAt(records.Count - 1);
        var objects = await ObjectQueries.ReferencesAsync(db, records, cancellationToken);
        return new ObjectReplay(records.Select(record => new ObjectRecord(record.TrackId,
            new ReplayedRecord(record.Id, record.StartedAt, record.EndedAt, record.ObservedAt,
                record.ReceivedAt, record.Value.Clone()) { Objects = objects[record.Id] })).ToArray(),
            more ? new ReplayRecordsCursor(records[^1].StartedAt, records[^1].Id) : null);
    }
}
