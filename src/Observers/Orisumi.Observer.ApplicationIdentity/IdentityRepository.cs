using System.Text.Json;
using Orisumi.Core;
using Orisumi.Observer.Sdk;

namespace Orisumi.Observers.ApplicationIdentity;

internal sealed class IdentityRepository(IObserverSession session, IdentityWork work)
{
    public async Task<EntityVersion?> ReadAsync(EntityId id, CancellationToken token)
    {
        var result = await session.RequestAsync("readCurrent", JsonLines.Json(new { entityId = id.Value }), cancellationToken: token);
        var version = result.GetProperty("version");
        return version.ValueKind == JsonValueKind.Null ? null : VersionJson.ReadSaved(version);
    }
    public async Task<List<EntityVersion>> ListAsync(string resource, CancellationToken token)
    {
        var results = new List<EntityVersion>();
        string? after = null;
        do
        {
            var page = await session.RequestAsync("listEntities", JsonLines.Json(new { resourceName = resource, limit = 1000, after }), cancellationToken: token);
            results.AddRange(page.GetProperty("versions").EnumerateArray().Select(VersionJson.ReadSaved));
            after = page.GetProperty("next").GetString();
        } while (after is not null);
        return results;
    }
    public async Task<List<EntityVersion>> PeriodsAsync(EntityId application, CancellationToken token)
    {
        var results = new List<EntityVersion>();
        var schemas = new Dictionary<VersionId, bool>();
        string? after = null;
        do
        {
            var page = await session.RequestAsync("listReferrers", JsonLines.Json(new { entityId = application.Value, limit = 100, after }), cancellationToken: token);
            foreach (var item in page.GetProperty("referrers").EnumerateArray())
            {
                var version = VersionJson.ReadSaved(item.GetProperty("version"));
                if (version.SchemaVersionId is not { } schemaId) continue;
                if (!schemas.TryGetValue(schemaId, out var isPeriod))
                {
                    var response = await session.RequestAsync("versions", JsonLines.Json(new { versionIds = new[] { schemaId.Value } }), cancellationToken: token);
                    var saved = response.GetProperty("versions");
                    isPeriod = saved.GetArrayLength() == 1 && saved[0].GetProperty("entityId").GetGuid() == IdentityData.PeriodSchemaId.Value;
                    schemas[schemaId] = isPeriod;
                }
                if (isPeriod) results.Add(version);
                if (results.Count == 100) return results;
            }
            after = page.GetProperty("next").GetString();
        } while (after is not null);
        return results;
    }
    public async Task<EntityVersion> EnsureAsync(EntityId id, VersionId? schema, VersionContent content, CancellationToken token)
    {
        var current = await ReadAsync(id, token);
        if (current is not null && JsonElement.DeepEquals(VersionJson.Content(current.Content), VersionJson.Content(content))) return current;
        await work.SubmitAsync([new(id, VersionId.New(), current?.VersionId, schema, content)], token);
        return (await ReadAsync(id, token))!;
    }
    public Task SubmitAsync(IEnumerable<VersionSubmission> versions, CancellationToken token) => work.SubmitAsync(versions, token);
}
