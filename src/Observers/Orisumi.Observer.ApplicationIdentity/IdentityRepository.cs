using System.Net;
using System.Text.Json;
using Orisumi.Core;
using Orisumi.Observer.Sdk;

namespace Orisumi.Observers.ApplicationIdentity;

internal sealed class IdentityRepository(DataClient data)
{
    internal async Task<EntityVersion[]> ApplicationsAsync(CancellationToken token)
    {
        // The foreground Observer declares this type, possibly after this Observer started.
        try { return (await data.ListAsync("entities?resourceName=local-applications&limit=1000", token)).Select(VersionJson.ReadSaved).ToArray(); }
        catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.NotFound) { return []; }
    }

    internal async Task<(Product[] Products, Membership[] Memberships)> CatalogAsync(CancellationToken token)
    {
        var products = (await data.ListAsync("entities?resourceName=application-products&limit=1000", token))
            .Select(VersionJson.ReadSaved).Select(IdentityData.Product).ToArray();
        var memberships = (await data.ListAsync("entities?resourceName=application-memberships&limit=1000", token)).Select(VersionJson.ReadSaved).ToArray();
        var targets = memberships.Select(v => ((BusinessContent)v.Content).Fields.GetProperty("productVersionId").GetGuid()).Distinct().ToArray();
        var entities = products.ToDictionary(p => p.Version.VersionId.Value, p => p.Version.EntityId);
        foreach (var chunk in targets.Where(id => !entities.ContainsKey(id)).Chunk(256))
        {
            var results = await data.VersionsAsync(chunk, token);
            foreach (var result in results.GetProperty("results").EnumerateArray())
                if (result.GetProperty("status").GetString() == "found")
                    entities[result.GetProperty("versionId").GetGuid()] = new(result.GetProperty("version").GetProperty("entityId").GetGuid());
        }
        return (products, memberships.Select(version =>
        {
            var fields = ((BusinessContent)version.Content).Fields;
            var target = fields.GetProperty("productVersionId").GetGuid();
            return new Membership(version, fields.GetProperty("identifier").Deserialize<ApplicationIdentifier>(IdentityData.Json)!,
                entities.TryGetValue(target, out var entity) && products.Any(p => p.Version.EntityId == entity) ? entity : null);
        }).ToArray());
    }

    internal async Task<EntityVersion[]> PeriodsAsync(EntityId application, CancellationToken token)
    {
        var page = await data.GetAsync($"entities/{application.Value}/referrers?resourceName=foreground-application-periods&path=%2Fcontent%2FlocalApplicationVersionId&limit=100", token);
        return page.GetProperty("referrers").EnumerateArray().Select(item => VersionJson.ReadSaved(item.GetProperty("version"))).ToArray();
    }
}
