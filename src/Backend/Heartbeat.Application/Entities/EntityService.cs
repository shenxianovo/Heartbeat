using Heartbeat.Core;

namespace Heartbeat.Application.Entities;

public sealed class EntityService(IEntityStore store, TimeProvider timeProvider)
{
    // VERSION-13: the writer supplies dependency order. A rejected version stops the batch;
    // earlier transactions remain committed, and later versions are not attempted.
    public async Task<IReadOnlyList<EntityVersion>> SubmitAsync(IEnumerable<VersionSubmission> versions,
        CancellationToken cancellationToken = default)
    {
        var result = new List<EntityVersion>();
        foreach (var version in versions)
            result.Add(await store.ExecuteAsync((transaction, token) => SaveAsync(transaction, version, token), cancellationToken));
        return result;
    }

    public Task<EntityVersion?> ReadVersionAsync(VersionId id, CancellationToken cancellationToken = default) =>
        store.ExecuteAsync((transaction, token) => transaction.ReadVersionAsync(id, token), cancellationToken);

    public Task<IReadOnlyList<EntityVersion>> ReadVersionsAsync(EntityId id, CancellationToken cancellationToken = default) =>
        store.ExecuteAsync((transaction, token) => transaction.ReadVersionsAsync(id, token), cancellationToken);

    public Task<EntityVersion?> ReadCurrentAsync(EntityId id, CancellationToken cancellationToken = default) =>
        store.ExecuteAsync(async (transaction, token) =>
        {
            var versions = await transaction.ReadVersionsAsync(id, token);
            return versions.Count == 0 ? null : versions[^1];
        }, cancellationToken);

    public async Task DeleteAsync(IEnumerable<EntityId> ids, CancellationToken cancellationToken = default)
    {
        foreach (var id in ids)
            await store.ExecuteAsync(async (transaction, token) =>
            {
                var versions = await transaction.ReadVersionsAsync(id, token);
                if (versions.Count == 0) return false;
                if (versions[0].Content is EntitySchema)
                    VersionRules.Require(!await transaction.HasBusinessVersionsAsync(id, token), "HISTORY-9", "Business versions still use this Schema.");
                await transaction.DeleteAsync(id, token);
                return true;
            }, cancellationToken);
    }

    private async Task<EntityVersion> SaveAsync(IEntityTransaction transaction, VersionSubmission version, CancellationToken token)
    {
        VersionRules.Require(!await transaction.IsEntityDeletedAsync(version.EntityId, token), "HISTORY-7", "Deleted entities cannot be restored.");
        VersionRules.Require(!await transaction.IsVersionDeletedAsync(version.VersionId, token), "VERSION-9", "Deleted VersionIds cannot be reused.");
        var existing = await transaction.ReadVersionAsync(version.VersionId, token);
        if (existing is not null)
        {
            VersionRules.Require(VersionRules.SameSubmission(existing.Submission, version), "VERSION-7", "VersionId already has different content.");
            return existing;
        }
        EntityVersion? schemaVersion = null;
        if (version.SchemaVersionId is { } schemaId)
            schemaVersion = await transaction.ReadVersionAsync(schemaId, token);
        VersionRules.Validate(version, schemaVersion?.Content as EntitySchema);
        var versions = await transaction.ReadVersionsAsync(version.EntityId, token);
        var current = versions.Count == 0 ? null : versions[^1];
        CheckPreviousVersion(version, versions);
        await CheckTypeAsync(transaction, version, current, schemaVersion, token);
        foreach (var reference in VersionRules.GetReferences(version, schemaVersion?.Content as EntitySchema))
            VersionRules.Require(await transaction.ReadVersionAsync(reference.Target, token) is not null,
                "ENTITY-6", $"Reference {reference.Position} does not point to a saved version.");
        if (version.Content is EntitySchema schema)
        {
            var owner = await transaction.FindSchemaAsync(schema.ResourceName, token);
            VersionRules.Require(owner is null || owner == version.EntityId, "SCHEMA-5", "resourceName belongs to another Schema.");
            VersionRules.Require(current?.Content is not EntitySchema previous || previous.ResourceName == schema.ResourceName,
                "SCHEMA-5", "resourceName cannot change.");
        }
        if (version.Content is Observation observation)
            await CheckObservationAsync(transaction, observation, current?.Content as Observation, token);
        var saved = new EntityVersion(version, timeProvider.GetUtcNow());
        await transaction.SaveAsync(saved, token);
        return saved;
    }

    private static void CheckPreviousVersion(VersionSubmission version, IReadOnlyList<EntityVersion> versions)
    {
        var current = versions.Count == 0 ? null : versions[^1];
        if (version.PreviousVersionId == current?.VersionId) return;
        VersionRules.Require(version.PreviousVersionId is not null
            && !versions.Any(saved => saved.VersionId == version.PreviousVersionId),
            "VERSION-6", "The entity already has a version newer than the submitted predecessor.");
        throw new ModelViolationException("VERSION-4", "previousVersionId must be null for a first version or refer to this entity's current version.");
    }

    private static async Task CheckTypeAsync(IEntityTransaction transaction, VersionSubmission version,
        EntityVersion? current, EntityVersion? schema, CancellationToken token)
    {
        if (current is null) return;
        VersionRules.Require(current.Category == version.Category, "ENTITY-3", "An entity cannot change category.");
        if (version.Content is not BusinessContent) return;
        var previousSchema = await transaction.ReadVersionAsync(current.SchemaVersionId!.Value, token);
        VersionRules.Require(previousSchema?.EntityId == schema!.EntityId, "ENTITY-3", "An entity cannot change business type.");
    }

    private static async Task CheckObservationAsync(IEntityTransaction transaction, Observation observation,
        Observation? previous, CancellationToken token)
    {
        var observer = await transaction.ReadVersionAsync(observation.ObserverVersionId, token);
        var content = await transaction.ReadVersionAsync(observation.ContentVersionId, token);
        VersionRules.Require(observer?.Content is BusinessContent && content?.Content is BusinessContent,
            "OBSERVATION-1", "Observer and content must be business versions.");
        if (observation.InputSetVersionId is { } inputs)
            VersionRules.Require((await transaction.ReadVersionAsync(inputs, token))?.Content is InputSet,
                "OBSERVATION-1", "inputSetVersionId must point to an InputSet.");
        if (previous is null) return;
        var original = await transaction.ReadVersionAsync(previous.ObserverVersionId, token);
        VersionRules.Require(original?.EntityId == observer!.EntityId, "OBSERVATION-10", "An Observation cannot change its Observer entity.");
    }
}
