using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Heartbeat.Core.DTOs.Facts;
using Heartbeat.Server.Entities;
using Heartbeat.Server.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Heartbeat.Server.Tests.Services;

public sealed partial class FactHttpTests
{
    [Fact]
    public async Task IndependentRelations_StayBoundToExactFactAcrossDevicesAndRevisions()
    {
        await SeedIndependentOwner();
        await using var app = CreateApplication();
        using var http = IndependentClient(app);
        var first = RelatedIndependentFact("first-machine");
        var second = RelatedIndependentFact("second-machine");
        await PostIndependent(http, first, HttpStatusCode.OK);
        await PostIndependent(http, second, HttpStatusCode.OK);
        var before = (await http.GetFromJsonAsync<List<FactResponse>>("/api/v1/users/alice/facts/segments"))!;
        var one = Assert.Single(before, f => f.Id == first.Id);
        var two = Assert.Single(before, f => f.Id == second.Id);
        Assert.Equal(one.FoiId, two.FoiId);
        Assert.NotEqual(one.DeviceId, two.DeviceId);
        var relation = Assert.Single(one.Relations);
        Assert.Equal(first.Id, relation.Evidence.GetProperty("factId").GetGuid());
        Assert.Equal(first.Start, relation.ValidFrom);
        Assert.Equal(first.End, relation.ValidTo);
        Assert.Equal(first.Id, Assert.Single((await http.GetFromJsonAsync<List<FactResponse>>(
            $"/api/v1/users/alice/facts/segments?deviceId={one.DeviceId}"))!).Id);

        first.Revision++;
        first.End = first.Start!.Value.AddSeconds(15);
        await PostIndependent(http, first, HttpStatusCode.OK);
        var shortened = Assert.Single((await http.GetFromJsonAsync<List<FactResponse>>(
            $"/api/v1/users/alice/facts/segments?deviceId={one.DeviceId}"))!);
        var shortenedRelation = Assert.Single(shortened.Relations);
        Assert.Equal(relation.Id, shortenedRelation.Id);
        Assert.Equal(first.End, shortenedRelation.ValidTo);
        first.Revision++;
        first.Relations.Clear();
        await PostIndependent(http, first, HttpStatusCode.OK);
        Assert.Empty((await http.GetFromJsonAsync<List<FactResponse>>(
            $"/api/v1/users/alice/facts/segments?deviceId={one.DeviceId}"))!);
        var after = (await http.GetFromJsonAsync<List<FactResponse>>("/api/v1/users/alice/facts/segments"))!;
        var withoutRelation = Assert.Single(after, f => f.Id == first.Id);
        Assert.Equal(3, withoutRelation.Revision);
        Assert.Equal(one.FoiId, withoutRelation.FoiId);
        Assert.Null(withoutRelation.DeviceId);
        Assert.Empty(withoutRelation.Relations);
        Assert.Equal(second.Id, Assert.Single((await http.GetFromJsonAsync<List<FactResponse>>(
            $"/api/v1/users/alice/facts/segments?deviceId={two.DeviceId}"))!).Id);
    }

    [Fact]
    public async Task IndependentRelations_InvalidLaterFactRollsBackEarlierRevisionAndNewFact()
    {
        await SeedIndependentOwner();
        await using var app = CreateApplication();
        using var http = IndependentClient(app);
        var existing = RelatedIndependentFact("established-machine");
        await PostIndependent(http, existing, HttpStatusCode.OK);
        var before = await http.GetFromJsonAsync<JsonElement>("/api/v1/users/alice/facts/segments");
        var devicesBefore = await http.GetFromJsonAsync<JsonElement>("/api/v1/users/alice/devices");
        existing.Revision++;
        existing.End = existing.Start!.Value.AddSeconds(30);
        existing.Result = JsonSerializer.SerializeToElement(new { replacement = true });
        existing.Relations.Clear();
        var added = RelatedIndependentFact("must-roll-back-machine");
        var invalid = RelatedIndependentFact("invalid-machine");
        invalid.Relations[0].Members[1] = new("device", new("account", "example", "wrong-kind"));
        using var rejected = await http.PostAsJsonAsync("/api/v1/observations",
            new ObservationUploadRequest { Facts = [existing, added, invalid] });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);
        Assert.True(JsonElement.DeepEquals(before,
            await http.GetFromJsonAsync<JsonElement>("/api/v1/users/alice/facts/segments")));
        Assert.True(JsonElement.DeepEquals(devicesBefore,
            await http.GetFromJsonAsync<JsonElement>("/api/v1/users/alice/devices")));
        // The rejected batch must leave no identity claim that prevents a later valid retry.
        await PostIndependent(http, added, HttpStatusCode.OK);
        Assert.Equal(2, (await http.GetFromJsonAsync<List<FactResponse>>("/api/v1/users/alice/facts/segments"))!.Count);
    }

    [Fact]
    public async Task IndependentRelations_PrivatePersonAndFactCollisionDoNotExposeAnotherOwner()
    {
        await SeedIndependentOwner();
        await using (var db = CreateDbContext())
        {
            db.Users.Add(new User { Id = "other", Username = "bob" });
            await db.SaveChangesAsync();
        }
        await using var app = CreateApplication();
        using var owner = IndependentClient(app);
        using var other = IndependentClient(app);
        other.DefaultRequestHeaders.Remove("X-Test-Owner");
        other.DefaultRequestHeaders.Add("X-Test-Owner", "other");
        using var established = await owner.PutAsync("/api/v1/me/person", null);
        established.EnsureSuccessStatusCode();
        var reference = (await established.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("reference").GetString()!;
        var fact = IndependentFact("event");
        fact.Foi = new("person", ObservationObjectScopes.Person, reference);
        fact.Aspect = "owner-private-aspect";
        fact.Result = JsonSerializer.SerializeToElement(new { secret = "owner-private-result" });
        await PostIndependent(owner, fact, HttpStatusCode.OK);
        var attacker = IndependentFact("event");
        attacker.Id = fact.Id;
        using var collision = await other.PostAsJsonAsync("/api/v1/observations",
            new ObservationUploadRequest { Facts = [attacker] });
        Assert.Equal(HttpStatusCode.Conflict, collision.StatusCode);
        var body = await collision.Content.ReadAsStringAsync();
        Assert.DoesNotContain("owner-private", body);
        Assert.DoesNotContain(reference, body);
        Assert.DoesNotContain(fact.CollectorId.ToString(), body);
        attacker.Id = Guid.NewGuid();
        attacker.Foi = fact.Foi;
        await PostIndependent(other, attacker, HttpStatusCode.UnprocessableEntity);
        using var hidden = await other.GetAsync("/api/v1/users/alice/facts/events");
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        Assert.Empty((await other.GetFromJsonAsync<List<FactResponse>>("/api/v1/users/bob/facts/events"))!);
        var preserved = Assert.Single((await owner.GetFromJsonAsync<List<FactResponse>>("/api/v1/users/alice/facts/events"))!);
        Assert.Equal(fact.Id, preserved.Id);
        Assert.Equal("owner-private-result", preserved.Result.GetProperty("secret").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IndependentRelations_PlatformEvidenceSurvivesRevisionAndCatalogRebindReplay(bool higherRevision)
    {
        await SeedIndependentOwner();
        await using var app = CreateApplication().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.Configure<AdministrationOptions>(options => options.Subjects = ["owner"])));
        using var http = IndependentClient(app);
        const string identityKey = "win:independent-evidence";
        var fact = RelatedIndependentFact("catalog-machine");
        fact.Foi = new("app", ObservationObjectScopes.AppIdentity, identityKey);
        fact.Relations[0].Members[0] = new("app", fact.Foi);
        await PostIndependent(http, fact, HttpStatusCode.OK);
        var original = Assert.Single((await http.GetFromJsonAsync<List<FactResponse>>("/api/v1/users/alice/facts/segments"))!);
        // Both an equivalent retry and a later snapshot may use the resolved product reference.
        if (higherRevision) fact.Revision++;
        fact.Foi = new("app", ObservationObjectScopes.App, original.Foi!.Key);
        fact.Relations[0].Members[0] = new("app", fact.Foi);
        await PostIndependent(http, fact, HttpStatusCode.OK);
        var before = Assert.Single((await http.GetFromJsonAsync<List<FactResponse>>("/api/v1/users/alice/facts/segments"))!);
        using var rebound = await http.PutAsJsonAsync($"/api/v1/admin/app-catalog/overrides/{Uri.EscapeDataString(identityKey)}",
            new { targetAppKey = "independent-corrected-product", newAppDisplayName = "Corrected Product" });
        Assert.True(rebound.IsSuccessStatusCode, await rebound.Content.ReadAsStringAsync());
        var after = Assert.Single((await http.GetFromJsonAsync<List<FactResponse>>("/api/v1/users/alice/facts/segments"))!);
        Assert.NotEqual(before.FoiId, after.FoiId);
        Assert.Equal("independent-corrected-product", after.Foi!.Key);
        Assert.Equal(before.Id, after.Id);
        Assert.Equal(before.Revision, after.Revision);
        Assert.Equal(before.Start, after.Start);
        Assert.Equal(before.End, after.End);
        Assert.True(JsonElement.DeepEquals(before.Result, after.Result));
        Assert.Equal(after.FoiId, Assert.Single(Assert.Single(after.Relations).Members, m => m.Role == "app").Object.Id);
        // Replay the exact accepted snapshot: maintenance must not force the producer to rewrite it.
        await PostIndependent(http, fact, HttpStatusCode.OK);
        var changed = JsonSerializer.Deserialize<ObservationSnapshot>(JsonSerializer.Serialize(fact))!;
        changed.Foi = new("app", ObservationObjectScopes.App, "never-observed-product");
        changed.Relations[0].Members[0] = new("app", changed.Foi);
        await PostIndependent(http, changed, HttpStatusCode.Conflict);
        changed.Revision++;
        await PostIndependent(http, changed, HttpStatusCode.Conflict);
        fact.Foi = new("app", ObservationObjectScopes.AppIdentity, identityKey);
        fact.Relations[0].Members[0] = new("app", fact.Foi);
        await PostIndependent(http, fact, HttpStatusCode.OK);
        Assert.Single((await http.GetFromJsonAsync<List<FactResponse>>("/api/v1/users/alice/facts/segments"))!);
    }

    [Theory]
    [InlineData("product")]
    [InlineData("platform")]
    [InlineData("removed")]
    public async Task IndependentRelations_CatalogMaintenanceFollowsOnlyCurrentAppRelationship(string replacement)
    {
        await SeedIndependentOwner();
        await using var app = CreateApplication().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.Configure<AdministrationOptions>(options => options.Subjects = ["owner"])));
        using var http = IndependentClient(app);
        const string oldIdentity = "win:relationship-old";
        const string newIdentity = "win:relationship-new";
        var fact = RelatedIndependentFact("relationship-machine");
        fact.Foi = new("machine", ObservationObjectScopes.Machine, "relationship-machine");
        fact.Relations[0].Members[0] = new("app", new("app", ObservationObjectScopes.AppIdentity, oldIdentity));
        await PostIndependent(http, fact, HttpStatusCode.OK);
        fact.Revision++;
        if (replacement == "removed") fact.Relations.Clear();
        else fact.Relations[0].Members[0] = new("app", new("app",
            replacement == "platform" ? ObservationObjectScopes.AppIdentity : ObservationObjectScopes.App,
            replacement == "platform" ? newIdentity : "replacement-product"));
        await PostIndependent(http, fact, HttpStatusCode.OK);
        var before = Assert.Single((await http.GetFromJsonAsync<List<FactResponse>>("/api/v1/users/alice/facts/segments"))!);
        using var correctedOldApp = await http.PutAsJsonAsync($"/api/v1/admin/app-catalog/overrides/{Uri.EscapeDataString(oldIdentity)}",
            new { targetAppKey = "corrected-old-product", newAppDisplayName = "Corrected old product" });
        Assert.True(correctedOldApp.IsSuccessStatusCode, await correctedOldApp.Content.ReadAsStringAsync());
        var after = Assert.Single((await http.GetFromJsonAsync<List<FactResponse>>("/api/v1/users/alice/facts/segments"))!);
        Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(before), JsonSerializer.SerializeToElement(after)),
            "Maintaining a formerly related App must not change the current Fact or its relationship.");
        if (replacement == "removed")
        {
            Assert.Empty(after.Relations);
            Assert.Null(after.AppId);
        }
        else
        {
            var appMember = Assert.Single(Assert.Single(after.Relations).Members, m => m.Role == "app");
            Assert.NotEqual("corrected-old-product", appMember.Object.Key);
        }
        await PostIndependent(http, fact, HttpStatusCode.OK);
        if (replacement == "platform")
        {
            using var correctedCurrentApp = await http.PutAsJsonAsync($"/api/v1/admin/app-catalog/overrides/{Uri.EscapeDataString(newIdentity)}",
                new { targetAppKey = "corrected-current-product", newAppDisplayName = "Corrected current product" });
            Assert.True(correctedCurrentApp.IsSuccessStatusCode, await correctedCurrentApp.Content.ReadAsStringAsync());
            var current = Assert.Single((await http.GetFromJsonAsync<List<FactResponse>>("/api/v1/users/alice/facts/segments"))!);
            Assert.Equal("corrected-current-product", Assert.Single(Assert.Single(current.Relations).Members, m => m.Role == "app").Object.Key);
            Assert.Equal(before.FoiId, current.FoiId);
            Assert.Equal(before.Id, current.Id);
            Assert.Equal(before.Revision, current.Revision);
            await PostIndependent(http, fact, HttpStatusCode.OK);
        }
    }

    private static ObservationSnapshot RelatedIndependentFact(string machine)
    {
        var fact = IndependentFact("segment");
        fact.Foi = new("app", ObservationObjectScopes.App, "same-product");
        fact.Relations = [new("observed-on", [new("app", fact.Foi),
            new("device", new("machine", ObservationObjectScopes.Machine, machine))])];
        return fact;
    }
}
