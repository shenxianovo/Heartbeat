using Heartbeat.Application.Recording;
using Heartbeat.Recording;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Persistence;

internal sealed class PostgresObjectStore(HeartbeatDbContext db) : IObjectStore
{
    private IQueryable<RecordingObject> Owned(Guid ownerId) =>
        db.Objects.AsNoTracking().Where(item => item.OwnerId == ownerId);

    private IQueryable<Guid> CatalogIds() => db.ObjectDescriptions.Select(item => item.ObjectId)
        .Concat(db.Timelines.Select(item => item.Id)).Concat(db.Collectors.Select(item => item.Id))
        .Concat(db.Tracks.Select(item => item.Id)).Concat(db.Hubs.Select(item => item.Id));

    private async Task<IReadOnlyList<ListedObject>> DescribeAsync(IQueryable<RecordingObject> objects, CancellationToken token)
    {
        var rows = await objects.Select(item => new
        {
            item.Id,
            Name = db.ObjectDescriptions.Where(description => description.ObjectId == item.Id).Select(description => description.Name).FirstOrDefault()
                ?? db.Collectors.Where(collector => collector.Id == item.Id).Select(collector => collector.DisplayName).FirstOrDefault()
                ?? db.Timelines.Where(timeline => timeline.Id == item.Id).Select(timeline => timeline.DisplayName).FirstOrDefault()
                ?? db.Tracks.Where(track => track.Id == item.Id).Select(track => track.Type).FirstOrDefault(),
            HubStatus = db.Hubs.Where(hub => hub.Id == item.Id).Select(hub => hub.StatusJson).FirstOrDefault(),
            Identifiers = db.ObjectBindings.Where(binding => binding.ObjectId == item.Id)
                .OrderBy(binding => binding.Namespace).ThenBy(binding => binding.Key).ThenBy(binding => binding.ScopeId)
                .Select(binding => new ListedIdentifier(binding.Namespace, binding.Key, binding.ScopeId)).ToArray(),
            Roles = db.RecordObjects.Where(link => link.ObjectId == item.Id).Select(link => link.Role).Distinct().OrderBy(role => role).ToArray(),
        }).ToListAsync(token);
        return rows.Select(row => new ListedObject(row.Id, row.Identifiers.FirstOrDefault()?.Namespace,
            row.Identifiers.FirstOrDefault()?.Key, row.Name ?? HubName(row.HubStatus), row.Roles)
            { Identifiers = row.Identifiers }).ToArray();
    }

    private static string? HubName(string? status)
    {
        if (status is null) return null;
        using var document = System.Text.Json.JsonDocument.Parse(status);
        return document.RootElement.GetProperty("displayName").GetString();
    }

    public async Task<IReadOnlyList<ListedObject>> ListAsync(Guid ownerId, IReadOnlyList<Guid>? contextObjectIds,
        Guid? objectId = null, int limit = 200, Guid? after = null, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 501) throw new ArgumentOutOfRangeException(nameof(limit));
        var objects = Owned(ownerId).Where(item => CatalogIds().Contains(item.Id));
        if (contextObjectIds is not null || objectId is not null)
        {
            var records = db.Records.ForObjects(db, objectId, contextObjectIds);
            objects = InRecordContext(objects, records);
        }
        if (after is { } cursor) objects = objects.Where(item => item.Id.CompareTo(cursor) > 0);
        return await DescribeAsync(objects.OrderBy(item => item.Id).Take(limit), cancellationToken);
    }

    private IQueryable<RecordingObject> InRecordContext(IQueryable<RecordingObject> objects, IQueryable<Record> records) =>
        objects.Where(item => records.Any(record => record.Id == item.Id || record.TrackId == item.Id ||
            db.Tracks.Any(track => track.Id == record.TrackId && (track.CollectorId == item.Id ||
                db.Collectors.Any(collector => collector.Id == track.CollectorId && collector.TimelineId == item.Id))) ||
            db.RecordObjects.Any(link => link.ObjectId == item.Id && link.RecordId == record.Id)));

    public async Task<ListedObject?> FindAsync(Guid ownerId, Guid objectId, CancellationToken cancellationToken = default) =>
        (await DescribeAsync(Owned(ownerId).Where(item => item.Id == objectId), cancellationToken)).SingleOrDefault();

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
