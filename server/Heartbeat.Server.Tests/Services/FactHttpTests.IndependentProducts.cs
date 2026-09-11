using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Heartbeat.Core.DTOs.Apps;
using Heartbeat.Core.DTOs.Facts;
using Heartbeat.Server.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Heartbeat.Server.Tests.Services;

public sealed partial class FactHttpTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task IndependentProducts_MergePartialRebindAndFallbackPreserveExactFactsAndReplay(bool machineFoi, bool catalogFallback)
    {
        await SeedIndependentOwner();
        await using var app = CreateApplication().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.Configure<AdministrationOptions>(options => options.Subjects = ["owner"])));
        using var http = IndependentClient(app);
        var firstIdentity = catalogFallback ? "win:chrome" : "win:independent-products-first";
        const string secondIdentity = "mac:com.example.independent-products-second";
        var first = ProductMaintenanceFact(firstIdentity, "first-product-machine", machineFoi);
        var second = ProductMaintenanceFact(secondIdentity, "second-product-machine", machineFoi);
        await PostIndependent(http, first, HttpStatusCode.OK);
        await PostIndependent(http, second, HttpStatusCode.OK);
        var before = await ReadProductFacts(http);
        var firstProduct = ProductObject(before[first.Id]);
        var secondProduct = ProductObject(before[second.Id]);
        var oldProductSnapshot = JsonSerializer.Deserialize<ObservationSnapshot>(JsonSerializer.Serialize(first))!;
        SetProductReference(oldProductSnapshot, new("app", ObservationObjectScopes.App, firstProduct.Key), machineFoi);
        await PostIndependent(http, oldProductSnapshot, HttpStatusCode.OK);

        using var merge = await http.PostAsJsonAsync("/api/v1/admin/apps/merge", new AppMergeRequest
        { SourceAppKey = firstProduct.Key, TargetAppKey = secondProduct.Key, DryRun = false });
        Assert.True(merge.IsSuccessStatusCode, await merge.Content.ReadAsStringAsync());
        var merged = await ReadProductFacts(http);
        Assert.Equal(secondProduct.Id, ProductObject(merged[first.Id]).Id);
        Assert.Equal(secondProduct.Id, ProductObject(merged[second.Id]).Id);
        AssertProductFactPreserved(before[first.Id], merged[first.Id]);
        await PostIndependent(http, oldProductSnapshot, HttpStatusCode.OK);
        await PostIndependent(http, first, HttpStatusCode.OK);

        var correctedKey = "independent-products-corrected";
        await SetProductOverride(http, firstIdentity, correctedKey);
        var split = await ReadProductFacts(http);
        Assert.Equal(correctedKey, ProductObject(split[first.Id]).Key);
        Assert.Equal(secondProduct.Id, ProductObject(split[second.Id]).Id);
        AssertProductFactPreserved(before[first.Id], split[first.Id]);
        AssertProductFactPreserved(before[second.Id], split[second.Id]);
        Assert.Equal(first.Id, Assert.Single((await http.GetFromJsonAsync<List<FactResponse>>(
            $"/api/v1/users/alice/facts/segments?appId={split[first.Id].AppId}"))!).Id);
        await PostIndependent(http, oldProductSnapshot, HttpStatusCode.OK);
        await PostIndependent(http, first, HttpStatusCode.OK);

        using var deleted = await http.DeleteAsync($"/api/v1/admin/app-catalog/overrides/{Uri.EscapeDataString(firstIdentity)}");
        Assert.True(deleted.IsSuccessStatusCode, await deleted.Content.ReadAsStringAsync());
        var fallbackResponse = (await deleted.Content.ReadFromJsonAsync<AppCatalogReconciliationResponse>())!;
        Assert.Equal(catalogFallback ? "catalog" : "provisional", fallbackResponse.FallbackSource);
        var fallback = await ReadProductFacts(http);
        Assert.Equal(fallbackResponse.TargetAppKey, ProductObject(fallback[first.Id]).Key);
        Assert.Equal(secondProduct.Id, ProductObject(fallback[second.Id]).Id);
        AssertProductFactPreserved(before[first.Id], fallback[first.Id]);
        await PostIndependent(http, oldProductSnapshot, HttpStatusCode.OK);
        await PostIndependent(http, first, HttpStatusCode.OK);
        await PostIndependent(http, second, HttpStatusCode.OK);
        var replayed = await ReadProductFacts(http);
        Assert.Equal(2, replayed.Count);
        Assert.Equal(ProductObject(fallback[first.Id]).Id, ProductObject(replayed[first.Id]).Id);
        Assert.Equal(ProductObject(fallback[second.Id]).Id, ProductObject(replayed[second.Id]).Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IndependentProducts_IdentityRebindRetainsProductReferencedWithoutPlatformEvidence(bool machineFoi)
    {
        await SeedIndependentOwner();
        await using var app = CreateApplication().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.Configure<AdministrationOptions>(options => options.Subjects = ["owner"])));
        using var http = IndependentClient(app);
        const string identity = "win:independent-direct-product";
        var platformFact = ProductMaintenanceFact(identity, "platform-machine", machineFoi);
        await PostIndependent(http, platformFact, HttpStatusCode.OK);
        var initial = (await ReadProductFacts(http))[platformFact.Id];
        var directFact = ProductMaintenanceFact(identity, "direct-machine", machineFoi);
        SetProductReference(directFact, new("app", ObservationObjectScopes.App, ProductObject(initial).Key), machineFoi);
        await PostIndependent(http, directFact, HttpStatusCode.OK);
        var before = (await ReadProductFacts(http))[directFact.Id];

        await SetProductOverride(http, identity, "independent-direct-corrected");
        var after = await ReadProductFacts(http);
        Assert.Equal("independent-direct-corrected", ProductObject(after[platformFact.Id]).Key);
        Assert.Equal(before.AppId, after[directFact.Id].AppId);
        Assert.Equal(ProductObject(before).Id, ProductObject(after[directFact.Id]).Id);
        AssertProductFactPreserved(before, after[directFact.Id]);
        await PostIndependent(http, directFact, HttpStatusCode.OK);
        await PostIndependent(http, platformFact, HttpStatusCode.OK);
        Assert.Equal(directFact.Id, Assert.Single((await http.GetFromJsonAsync<List<FactResponse>>(
            $"/api/v1/users/alice/facts/segments?appId={before.AppId}"))!).Id);
    }

    private static ObservationSnapshot ProductMaintenanceFact(string identity, string machine, bool machineFoi)
    {
        var fact = RelatedIndependentFact(machine);
        SetProductReference(fact, new("app", ObservationObjectScopes.AppIdentity, identity), machineFoi);
        if (machineFoi) fact.Foi = new("machine", ObservationObjectScopes.Machine, machine);
        return fact;
    }

    private static void SetProductReference(ObservationSnapshot fact, ObservationObjectReference reference, bool machineFoi)
    {
        if (!machineFoi) fact.Foi = reference;
        fact.Relations[0].Members[0] = new("app", reference);
    }

    private static ObjectSummary ProductObject(FactResponse fact) =>
        Assert.Single(Assert.Single(fact.Relations).Members, member => member.Role == "app").Object;

    private static async Task<Dictionary<Guid, FactResponse>> ReadProductFacts(HttpClient http) =>
        (await http.GetFromJsonAsync<List<FactResponse>>("/api/v1/users/alice/facts/segments"))!.ToDictionary(fact => fact.Id);

    private static async Task SetProductOverride(HttpClient http, string identity, string key)
    {
        using var response = await http.PutAsJsonAsync($"/api/v1/admin/app-catalog/overrides/{Uri.EscapeDataString(identity)}",
            new { targetAppKey = key, newAppDisplayName = key });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    private static void AssertProductFactPreserved(FactResponse before, FactResponse after)
    {
        Assert.Equal(before.Id, after.Id);
        Assert.Equal(before.CollectorId, after.CollectorId);
        Assert.Equal(before.Kind, after.Kind);
        Assert.Equal(before.Revision, after.Revision);
        Assert.Equal(before.Start, after.Start);
        Assert.Equal(before.End, after.End);
        Assert.Equal(before.DeviceId, after.DeviceId);
        Assert.Null(after.StreamId);
        Assert.True(JsonElement.DeepEquals(before.Result, after.Result));
        var beforeRelation = Assert.Single(before.Relations);
        var afterRelation = Assert.Single(after.Relations);
        Assert.Equal(beforeRelation.Id, afterRelation.Id);
        Assert.Equal(beforeRelation.ValidFrom, afterRelation.ValidFrom);
        Assert.Equal(beforeRelation.ValidTo, afterRelation.ValidTo);
        Assert.True(JsonElement.DeepEquals(beforeRelation.Evidence, afterRelation.Evidence));
    }
}
