using System.Text.Json;
using Orisumi.Application.Entities;
using Orisumi.Application.Observers;
using Orisumi.Core;

namespace Orisumi.Observers.ApplicationIdentity;

internal sealed class IdentityRepository(EntityService entities, ServerObserverSession session)
{
    public async Task<EntityVersion?> ReadAsync(EntityId id, CancellationToken token)
    {
        try { return await entities.ReadCurrentAsync(id, token); }
        catch (EntityDeletedException) { return null; }
    }

    public async Task<List<EntityVersion>> ListAsync(string resource, CancellationToken token)
    {
        var results = new List<EntityVersion>();
        EntityId? after = null;
        do
        {
            IReadOnlyList<EntityVersion> page;
            try { page = await entities.ListEntitiesAsync(new(Limit: 1000, After: after), resource, token); }
            catch (ResourceNotFoundException) when (after is null) { return []; }
            results.AddRange(page);
            if (page.Count < 1000) break;
            after = page[^1].EntityId;
        } while (true);
        return results;
    }

    public async Task<List<EntityVersion>> PeriodsAsync(EntityId application, CancellationToken token)
    {
        var results = new List<EntityVersion>();
        var schemas = new Dictionary<VersionId, bool>();
        VersionId? after = null;
        do
        {
            var page = await entities.ListReferrersAsync(new(application, Limit: 100, After: after), token) ?? [];
            foreach (var item in page)
            {
                var version = item.Version;
                if (version.SchemaVersionId is not { } schemaId) continue;
                if (!schemas.TryGetValue(schemaId, out var isPeriod))
                {
                    EntityVersion? schema;
                    try { schema = await entities.ReadVersionAsync(schemaId, token); }
                    catch (EntityDeletedException) { continue; }
                    if (schema is null) continue;
                    isPeriod = schema.EntityId == IdentityData.PeriodSchemaId;
                    schemas[schemaId] = isPeriod;
                }
                if (isPeriod) results.Add(version);
                if (results.Count == 100) return results;
            }
            if (page.Count < 100) break;
            after = page[^1].Version.VersionId;
        } while (true);
        return results;
    }

    public async Task<EntityVersion> EnsureAsync(EntityId id, VersionId? schema, VersionContent content, CancellationToken token)
    {
        var current = await ReadAsync(id, token);
        if (current is not null && JsonElement.DeepEquals(VersionJson.Content(current.Content), VersionJson.Content(content))) return current;
        await SubmitAsync([new(id, VersionId.New(), current?.VersionId, schema, content)], token);
        return (await ReadAsync(id, token))!;
    }

    public Task SubmitAsync(IEnumerable<VersionSubmission> versions, CancellationToken token) => session.SubmitAsync(versions, token);
}
