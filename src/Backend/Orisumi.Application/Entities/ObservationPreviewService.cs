using Orisumi.Core;

namespace Orisumi.Application.Entities;

public sealed class ObservationPreviewService(IEntityStore store, ObservationPreviewCache cache, TimeProvider timeProvider)
{
    public Task<ObservationPreview> PutAsync(EntityId id, VersionId? previous, long sequence, Observation content,
        CancellationToken cancellationToken = default) => store.ExecuteAsync(async (transaction, token) =>
    {
        VersionRules.Require(VersionRules.IsUuid7(id.Value), "ENTITY-2", "EntityId must be a UUIDv7.");
        VersionRules.Require(previous is null || VersionRules.IsUuid7(previous.Value.Value), "VERSION-12", "Invalid previous VersionId.");
        VersionRules.Require(!await transaction.IsEntityDeletedAsync(id, token), "HISTORY-7", "Deleted entities cannot be restored.");
        var current = await transaction.ReadCurrentAsync(id, token);
        VersionRules.Require(previous == current?.VersionId, "VERSION-6", "The preview must follow the current version.");
        VersionRules.Require(current is null || current.Content is Observation, "ENTITY-3", "Only Observations have observation previews.");
        content = VersionTime.Normalize(content);
        VersionRules.ValidateObservation(content);
        await EntityService.CheckObservationAsync(transaction, content, current?.Content as Observation, token);
        var target = (await transaction.ReadVersionAsync(content.ContentVersionId, token))!;
        var schema = (await transaction.ReadVersionAsync(target.SchemaVersionId!.Value, token))!;
        return cache.Put(new(id, previous, sequence, content, VersionTime.Normalize(timeProvider.GetUtcNow())), schema.EntityId);
    }, cancellationToken);

    public Task<ObservationPreview?> ReadAsync(EntityId id, CancellationToken cancellationToken = default) =>
        store.ExecuteAsync((transaction, token) => ReadCurrentAsync(transaction, id, token), cancellationToken);

    public Task<IReadOnlyList<ObservationPreview>> ListAsync(string? resourceName, CancellationToken cancellationToken = default) =>
        store.ExecuteAsync<IReadOnlyList<ObservationPreview>>(async (transaction, token) =>
        {
            var schema = resourceName is null ? (EntityId?)null
                : await transaction.FindSchemaAsync(resourceName, token) ?? throw new ResourceNotFoundException();
            var result = new List<ObservationPreview>();
            foreach (var preview in cache.List(schema))
                if (await ReadCurrentAsync(transaction, preview.EntityId, token) is { } current) result.Add(current);
            return result;
        }, cancellationToken);

    private async Task<ObservationPreview?> ReadCurrentAsync(IEntityTransaction transaction, EntityId id, CancellationToken token)
    {
        var preview = cache.Read(id);
        if (preview is null) return null;
        // Also handles versions committed by another service instance before cache invalidation.
        if (await transaction.IsEntityDeletedAsync(id, token)
            || (await transaction.ReadCurrentAsync(id, token))?.VersionId != preview.PreviousVersionId)
        {
            cache.Remove(id);
            return null;
        }
        return preview;
    }
}
