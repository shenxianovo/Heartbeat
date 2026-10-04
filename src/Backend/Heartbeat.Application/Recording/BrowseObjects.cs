namespace Heartbeat.Application.Recording;

public sealed record ListedObject(Guid Id, string? Namespace, string? Key, string? Name, IReadOnlyList<string> Roles)
{
    public IReadOnlyList<ListedIdentifier> Identifiers { get; init; } = [];
}
public sealed record ListedIdentifier(string Namespace, string Key, Guid? ScopeId);
public sealed record ObjectRecord(Guid TrackId, ReplayedRecord Record);
public sealed record ObjectReplay(IReadOnlyList<ObjectRecord> Records, ReplayRecordsCursor? NextCursor);

public interface IObjectStore
{
    Task<IReadOnlyList<ListedObject>> ListAsync(Guid ownerId, IReadOnlyList<Guid>? contextObjectIds,
        Guid? objectId = null, int limit = 200, Guid? after = null, CancellationToken cancellationToken = default);
    Task<ListedObject?> FindAsync(Guid ownerId, Guid objectId, CancellationToken cancellationToken = default);
    Task<ObjectReplay> ReplayAsync(Guid ownerId, Guid objectId, IReadOnlyList<Guid>? contextObjectIds,
        DateTimeOffset? from, DateTimeOffset? until, int limit, ReplayRecordsCursor? cursor,
        CancellationToken cancellationToken = default);
}
