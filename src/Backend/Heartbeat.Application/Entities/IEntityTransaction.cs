using Heartbeat.Core;

namespace Heartbeat.Application.Entities;

public interface IEntityTransaction
{
    Task<EntityVersion?> ReadVersionAsync(VersionId id, CancellationToken cancellationToken);
    // Returns the complete chain in save order, from the first version to the current version.
    Task<IReadOnlyList<EntityVersion>> ReadVersionsAsync(EntityId id, CancellationToken cancellationToken);
    Task<bool> IsEntityDeletedAsync(EntityId id, CancellationToken cancellationToken);
    Task<bool> IsVersionDeletedAsync(VersionId id, CancellationToken cancellationToken);
    Task<EntityId?> FindSchemaAsync(string resourceName, CancellationToken cancellationToken);
    Task<bool> HasBusinessVersionsAsync(EntityId schemaId, CancellationToken cancellationToken);
    Task SaveAsync(EntityVersion version, CancellationToken cancellationToken);
    // Removes every version's content; retains only the occupied EntityId and VersionIds.
    Task DeleteAsync(EntityId id, CancellationToken cancellationToken);
}
