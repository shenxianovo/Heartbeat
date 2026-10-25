using Heartbeat.Core;

namespace Heartbeat.Application.Entities;

public sealed class EntityService(IEntityStore store, TimeProvider timeProvider)
{
    // VERSION-13: rejection commits the prefix; an unexpected failure rolls back the whole operation.
    public Task<SubmissionResult> SubmitAsync(IEnumerable<VersionSubmission> versions,
        CancellationToken cancellationToken = default) => store.ExecuteAsync(async (transaction, token) =>
    {
        var saved = new List<EntityVersion>();
        foreach (var version in versions)
        {
            await transaction.CreateSavepointAsync(token);
            try
            {
                saved.Add(await SaveAsync(transaction, VersionTime.Normalize(version), token));
                await transaction.ReleaseSavepointAsync(token);
            }
            catch (ModelViolationException exception)
            {
                await transaction.RollbackSavepointAsync(token);
                return new SubmissionResult(saved, new(saved.Count, version.VersionId, exception.Rule, exception.Message));
            }
        }
        return new SubmissionResult(saved, null);
    }, cancellationToken);

    public Task<EntityVersion?> ReadVersionAsync(VersionId id, CancellationToken cancellationToken = default) =>
        store.ExecuteAsync(async (transaction, token) =>
        {
            if (await transaction.IsVersionDeletedAsync(id, token)) throw new EntityDeletedException();
            return await transaction.ReadVersionAsync(id, token);
        }, cancellationToken);

    public Task<IReadOnlyList<EntityVersion>> ReadVersionsAsync(EntityId id, CancellationToken cancellationToken = default) =>
        store.ExecuteAsync(async (transaction, token) =>
        {
            if (await transaction.IsEntityDeletedAsync(id, token)) throw new EntityDeletedException();
            return await transaction.ReadVersionsAsync(id, token);
        }, cancellationToken);

    public Task<EntityVersion?> ReadCurrentAsync(EntityId id, CancellationToken cancellationToken = default) =>
        store.ExecuteAsync(async (transaction, token) =>
        {
            if (await transaction.IsEntityDeletedAsync(id, token)) throw new EntityDeletedException();
            return await transaction.ReadCurrentAsync(id, token);
        }, cancellationToken);

    public Task<bool> DeleteAsync(EntityId id, CancellationToken cancellationToken = default) =>
        store.ExecuteAsync(async (transaction, token) =>
        {
            if (await transaction.IsEntityDeletedAsync(id, token)) return true;
            var current = await transaction.ReadCurrentAsync(id, token);
            if (current is null) return false;
            if (current.Content is EntitySchema)
                VersionRules.Require(!await transaction.HasBusinessVersionsAsync(id, token), "HISTORY-9", "Business versions still use this Schema.");
            await transaction.DeleteAsync(id, token);
            return true;
        }, cancellationToken);

    public Task<IReadOnlyList<EntityVersion>> ListEntitiesAsync(EntityQuery query, string? resourceName,
        CancellationToken cancellationToken = default) => store.ExecuteAsync(async (transaction, token) =>
    {
        var schema = await ResolveSchemaAsync(transaction, resourceName, token);
        return await transaction.ListEntitiesAsync(query with { SchemaId = schema }, token);
    }, cancellationToken);

    public Task<IReadOnlyList<EntityVersion>> ListObservationsAsync(ObservationQuery query, string? resourceName,
        CancellationToken cancellationToken = default) => store.ExecuteAsync(async (transaction, token) =>
    {
        var schema = await ResolveSchemaAsync(transaction, resourceName, token);
        return await transaction.ListObservationsAsync(query with { SchemaId = schema }, token);
    }, cancellationToken);

    public Task<IReadOnlyList<Referrer>?> ListReferrersAsync(ReferrerQuery query,
        CancellationToken cancellationToken = default) => store.ExecuteAsync<IReadOnlyList<Referrer>?>(async (transaction, token) =>
    {
        // HISTORY-8: a deleted target still has readable referrers.
        if (!await transaction.EntityExistsAsync(query.Target, token)) return null;
        return await transaction.ListReferrersAsync(query, token);
    }, cancellationToken);

    private static async Task<EntityId?> ResolveSchemaAsync(IEntityTransaction transaction, string? resourceName, CancellationToken token) =>
        resourceName is null ? null : await transaction.FindSchemaAsync(resourceName, token) ?? throw new ResourceNotFoundException();

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
        var current = await transaction.ReadCurrentAsync(version.EntityId, token);
        await CheckPreviousVersionAsync(transaction, version, current, token);
        await CheckTypeAsync(transaction, version, current, schemaVersion, token);
        var references = new List<ResolvedReference>();
        foreach (var reference in VersionRules.GetReferences(version, schemaVersion?.Content as EntitySchema))
        {
            var target = await transaction.ReadVersionAsync(reference.Target, token);
            VersionRules.Require(target is not null,
                "ENTITY-6", $"Reference {reference.Position} does not point to a saved version.");
            if (reference.Position is not ("/previousVersionId" or "/schemaVersionId"))
                references.Add(new(reference.Position, reference.Target, target!.EntityId));
        }
        if (version.Content is EntitySchema schema)
        {
            var owner = await transaction.FindSchemaAsync(schema.ResourceName, token);
            VersionRules.Require(owner is null || owner == version.EntityId, "SCHEMA-5", "resourceName belongs to another Schema.");
            VersionRules.Require(current?.Content is not EntitySchema previous || previous.ResourceName == schema.ResourceName,
                "SCHEMA-5", "resourceName cannot change.");
        }
        if (version.Content is Observation observation)
            await CheckObservationAsync(transaction, observation, current?.Content as Observation, token);
        var saved = new EntityVersion(version, VersionTime.Normalize(timeProvider.GetUtcNow()));
        await transaction.SaveAsync(saved, references, token);
        return saved;
    }

    private static async Task CheckPreviousVersionAsync(IEntityTransaction transaction, VersionSubmission version,
        EntityVersion? current, CancellationToken token)
    {
        if (version.PreviousVersionId == current?.VersionId) return;
        var owner = version.PreviousVersionId is { } previous
            ? await transaction.FindVersionEntityAsync(previous, token) : null;
        VersionRules.Require(current is null || version.PreviousVersionId is not null && owner != version.EntityId,
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
        var original = await transaction.FindVersionEntityAsync(previous.ObserverVersionId, token);
        VersionRules.Require(original == observer!.EntityId, "OBSERVATION-10", "An Observation cannot change its Observer entity.");
    }
}
