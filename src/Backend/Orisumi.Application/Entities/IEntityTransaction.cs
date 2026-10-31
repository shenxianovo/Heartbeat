using Orisumi.Core;

namespace Orisumi.Application.Entities;

public interface IEntityTransaction
{
    Task<EntityVersion?> ReadCurrentAsync(EntityId id, CancellationToken cancellationToken);
    Task<IReadOnlyList<EntityVersion>> ListEntitiesAsync(EntityQuery query, CancellationToken cancellationToken);
    Task<IReadOnlyList<EntityVersion>> ListObservationsAsync(ObservationQuery query, CancellationToken cancellationToken);
    Task<IReadOnlyList<Referrer>> ListReferrersAsync(ReferrerQuery query, CancellationToken cancellationToken);
    Task<EntityId?> FindVersionEntityAsync(VersionId id, CancellationToken cancellationToken);
    Task<EntityVersion?> ReadVersionAsync(VersionId id, CancellationToken cancellationToken);
    // Returns the complete chain in save order, from the first version to the current version.
    Task<IReadOnlyList<EntityVersion>> ReadVersionsAsync(EntityId id, CancellationToken cancellationToken);
    Task<bool> EntityExistsAsync(EntityId id, CancellationToken cancellationToken);
    Task<bool> IsEntityDeletedAsync(EntityId id, CancellationToken cancellationToken);
    Task<bool> IsVersionDeletedAsync(VersionId id, CancellationToken cancellationToken);
    Task<EntityId?> FindSchemaAsync(string resourceName, CancellationToken cancellationToken);
    Task<bool> HasBusinessVersionsAsync(EntityId schemaId, CancellationToken cancellationToken);
    Task SaveAsync(EntityVersion version, IReadOnlyList<ResolvedReference> references, IReadOnlyList<BinaryValue> contents, CancellationToken cancellationToken);
    Task<long?> ContentSizeAsync(string digest, CancellationToken cancellationToken);
    Task UploadContentAsync(string digest, byte[] data, DateTimeOffset uploadedAt, CancellationToken cancellationToken);
    Task<byte[]?> ReadContentAsync(string digest, CancellationToken cancellationToken);
    Task PruneContentsAsync(DateTimeOffset before, CancellationToken cancellationToken);
    // Removes every version's content; retains only the occupied EntityId and VersionIds.
    Task DeleteAsync(EntityId id, CancellationToken cancellationToken);
}
