using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Heartbeat.Core.DTOs.Facts;
using Heartbeat.Core.DTOs.Persons;

namespace Heartbeat.Server.Tests.Services;

public sealed partial class FactHttpTests
{
    [Fact]
    public async Task IndependentPerson_ExactDeviceEvidenceAndRevisedWindowControlMembership()
    {
        await SeedIndependentOwner();
        await using var app = CreateApplication();
        using var http = IndependentClient(app);
        var first = RelatedIndependentFact("used-machine");
        var second = RelatedIndependentFact("other-machine");
        await PostIndependent(http, first, HttpStatusCode.OK);
        await PostIndependent(http, second, HttpStatusCode.OK);
        var original = (await http.GetFromJsonAsync<List<FactResponse>>("/api/v1/users/alice/facts/segments"))!;
        var used = Assert.Single(Assert.Single(original, f => f.Id == first.Id).Relations).Members.Single(m => m.Role == "device").Object;
        using var established = await http.PutAsync("/api/v1/me/person", null);
        established.EnsureSuccessStatusCode();
        using var linked = await http.PostAsJsonAsync("/api/v1/me/person/associations",
            new { objectId = used.Id, start = first.Start!.Value.AddSeconds(30), end = first.Start.Value.AddSeconds(90) });
        linked.EnsureSuccessStatusCode();
        const string path = "/api/v1/me/person/facts/segments";
        var visible = Assert.Single((await http.GetFromJsonAsync<PersonFactPage>(path))!.Items);
        Assert.Equal(first.Id, visible.Fact.Id);
        Assert.Equal(60, visible.EffectiveSeconds);
        first.Revision++;
        first.End = first.Start.Value.AddSeconds(20);
        await PostIndependent(http, first, HttpStatusCode.OK);
        Assert.Empty((await http.GetFromJsonAsync<PersonFactPage>(path))!.Items);
        second.Revision++;
        second.Relations[0].Members[1] = first.Relations[0].Members[1];
        await PostIndependent(http, second, HttpStatusCode.OK);
        await PostIndependent(http, second, HttpStatusCode.OK);
        var changed = Assert.Single((await http.GetFromJsonAsync<PersonFactPage>(path))!.Items);
        Assert.Equal(second.Id, changed.Fact.Id);
        Assert.Equal(60, changed.EffectiveSeconds);
        Assert.Single(changed.Fact.Relations);
        second.Revision++;
        second.Relations.Clear();
        await PostIndependent(http, second, HttpStatusCode.OK);
        Assert.Empty((await http.GetFromJsonAsync<PersonFactPage>(path))!.Items);
    }

    [Theory]
    [InlineData("segment")]
    [InlineData("event")]
    public async Task IndependentPerson_DirectPersonNeedsNoUsageAssociation(string kind)
    {
        await SeedIndependentOwner();
        await using var app = CreateApplication();
        using var http = IndependentClient(app);
        using var established = await http.PutAsync("/api/v1/me/person", null);
        established.EnsureSuccessStatusCode();
        var person = (await established.Content.ReadFromJsonAsync<PersonResponse>())!;
        var fact = IndependentFact(kind);
        fact.Foi = new("person", ObservationObjectScopes.Person, person.Reference.ToString());
        await PostIndependent(http, fact, HttpStatusCode.OK);
        var page = (await http.GetFromJsonAsync<PersonFactPage>($"/api/v1/me/person/facts/{kind}s"))!;
        var item = Assert.Single(page.Items);
        Assert.Equal(fact.Id, item.Fact.Id);
        Assert.Equal(kind, item.Fact.Kind);
        Assert.Equal(person.Id, item.Fact.FoiId);
        Assert.Empty(item.Fact.Relations);
        Assert.Null(item.Fact.StreamId);
        if (kind == "segment") Assert.Equal(120, item.EffectiveSeconds);
    }

    [Theory]
    [InlineData("segment")]
    [InlineData("event")]
    public async Task IndependentPerson_AccountWithoutProfileRetainsContractThroughAssociationMaintenance(string kind)
    {
        await SeedIndependentOwner();
        await using var app = CreateApplication();
        using var http = IndependentClient(app);
        var fact = IndependentFact(kind);
        await PostIndependent(http, fact, HttpStatusCode.OK);
        var path = $"/api/v1/users/alice/facts/{kind}s";
        var original = Assert.Single((await http.GetFromJsonAsync<List<FactResponse>>(path))!);
        Assert.Null(original.StreamId);
        Assert.Null(original.DeviceId);
        Assert.Null(original.AppId);
        Assert.Empty(original.Relations);
        Assert.Equal(fact.Id, Assert.Single((await http.GetFromJsonAsync<List<FactResponse>>($"{path}?foiId={original.FoiId}"))!).Id);
        using var established = await http.PutAsync("/api/v1/me/person", null);
        established.EnsureSuccessStatusCode();
        var settings = (await http.GetFromJsonAsync<PersonSettingsResponse>("/api/v1/me/person"))!;
        Assert.Equal(original.FoiId, Assert.Single(settings.Objects).Id);
        var personPath = $"/api/v1/me/person/facts/{kind}s";
        Assert.Empty((await http.GetFromJsonAsync<PersonFactPage>(personPath))!.Items);
        var start = fact.Start ?? fact.OccurredAt!.Value;
        using var created = await http.PostAsJsonAsync("/api/v1/me/person/associations",
            new { objectId = original.FoiId, start, end = start.AddSeconds(30) });
        created.EnsureSuccessStatusCode();
        var link = (await created.Content.ReadFromJsonAsync<PersonAssociationResponse>())!;
        var page = (await http.GetFromJsonAsync<PersonFactPage>(personPath))!;
        var item = Assert.Single(page.Items);
        Assert.Equal(kind, item.Fact.Kind);
        Assert.Null(Assert.Single(page.Sources).Source);
        Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(original), JsonSerializer.SerializeToElement(item.Fact)));
        if (kind == "segment") Assert.Equal(30, item.EffectiveSeconds);
        else Assert.Empty(item.EffectiveIntervals);
        using var corrected = await http.PutAsJsonAsync($"/api/v1/me/person/associations/{link.Id}",
            new { objectId = original.FoiId, start = start.AddSeconds(30), end = start.AddSeconds(45) });
        corrected.EnsureSuccessStatusCode();
        var changed = (await http.GetFromJsonAsync<PersonFactPage>(personPath))!;
        if (kind == "segment") Assert.Equal(15, Assert.Single(changed.Items).EffectiveSeconds);
        else Assert.Empty(changed.Items);
        using var removed = await http.DeleteAsync($"/api/v1/me/person/associations/{link.Id}");
        removed.EnsureSuccessStatusCode();
        Assert.Empty((await http.GetFromJsonAsync<PersonFactPage>(personPath))!.Items);
        await PostIndependent(http, fact, HttpStatusCode.OK);
        Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(original),
            JsonSerializer.SerializeToElement(Assert.Single((await http.GetFromJsonAsync<List<FactResponse>>(path))!))));
    }
}
