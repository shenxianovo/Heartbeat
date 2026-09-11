using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Heartbeat.Core;
using Heartbeat.Core.DTOs.Facts;
using Heartbeat.Server.Entities;

namespace Heartbeat.Server.Tests.Services;

public sealed partial class FactHttpTests
{
    [Theory]
    [InlineData("segment")]
    [InlineData("event")]
    public async Task IndependentObservation_PreservesProducerIdentityAndUnknownResultWithoutStreams(string kind)
    {
        await using (var db = CreateDbContext())
        {
            db.Users.Add(new User { Id = "owner", Username = "alice" });
            await db.SaveChangesAsync();
        }
        await using var app = CreateApplication();
        using var http = app.CreateClient();
        http.DefaultRequestHeaders.Add(HeartbeatProtocol.VersionHeader, HeartbeatProtocol.RequiredVersion);
        http.DefaultRequestHeaders.Add("X-Test-Owner", "owner");
        var id = Guid.NewGuid();
        var collector = Guid.NewGuid();
        var start = DateTimeOffset.UtcNow.AddMinutes(-5);
        var result = JsonSerializer.SerializeToElement(new { unknown = new { nested = new[] { 1, 2 }, future = "preserved" } });
        var fact = new { id, kind, collectorId = collector, foi = new { kind = "account", scope = "example", key = "account-a" },
            aspect = "future-aspect", revision = 1, result, relations = Array.Empty<object>(),
            start = kind == "segment" ? (DateTimeOffset?)start : null,
            end = kind == "segment" ? (DateTimeOffset?)start.AddMinutes(1) : null,
            occurredAt = kind == "event" ? (DateTimeOffset?)start : null };
        using var posted = await http.PostAsJsonAsync("/api/v1/observations", new { facts = new[] { fact } });
        Assert.True(posted.IsSuccessStatusCode, await posted.Content.ReadAsStringAsync());
        using var retry = await http.PostAsJsonAsync("/api/v1/observations", new { facts = new[] { fact } });
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        var read = Assert.Single((await http.GetFromJsonAsync<List<FactResponse>>($"/api/v1/users/alice/facts/{kind}s"))!);
        Assert.Equal(id, read.Id);
        Assert.Equal(collector, read.CollectorId);
        Assert.Equal("future-aspect", read.Aspect);
        Assert.Equal("account", read.Foi!.Kind);
        Assert.Empty(read.Relations);
        Assert.True(JsonElement.DeepEquals(result, read.Payload));
    }
    [Theory]
    [InlineData("segment")]
    [InlineData("event")]
    public async Task IndependentObservation_RevisionOrdersSnapshotsAndProtectsFactIdentity(string kind)
    {
        await SeedIndependentOwner();
        await using var app = CreateApplication();
        using var http = IndependentClient(app);
        var fact = IndependentFact(kind);
        await PostIndependent(http, fact, HttpStatusCode.OK);
        var original = JsonSerializer.Serialize(fact);
        fact.Revision = 3;
        fact.Result = JsonSerializer.SerializeToElement(new { corrected = true });
        if (kind == "segment") fact.End = fact.Start!.Value.AddSeconds(30);
        await PostIndependent(http, fact, HttpStatusCode.OK);
        await PostIndependent(http, JsonSerializer.Deserialize<ObservationSnapshot>(original)!, HttpStatusCode.OK);
        var latest = JsonSerializer.Serialize(fact);
        foreach (var change in new Action<ObservationSnapshot>[]
        {
            f => f.CollectorId = Guid.NewGuid(),
            f => f.Foi = new("account", "example", "other"),
            f => f.Aspect = "different-aspect",
            f => { f.Kind = kind == "segment" ? "event" : "segment"; f.Start = f.Kind == "segment" ? fact.OccurredAt : null;
                f.End = f.Start; f.OccurredAt = f.Kind == "event" ? fact.Start : null; },
            f => { if (kind == "segment") f.Start = f.Start!.Value.AddSeconds(-1); else f.OccurredAt = f.OccurredAt!.Value.AddSeconds(-1); }
        })
        {
            var invalid = JsonSerializer.Deserialize<ObservationSnapshot>(latest)!;
            invalid.Revision++;
            change(invalid);
            await PostIndependent(http, invalid, HttpStatusCode.Conflict);
        }
        fact.Result = JsonSerializer.SerializeToElement(new { conflict = true });
        await PostIndependent(http, fact, HttpStatusCode.Conflict);
        var row = Assert.Single((await http.GetFromJsonAsync<List<FactResponse>>($"/api/v1/users/alice/facts/{kind}s"))!);
        Assert.Equal(3, row.Revision);
        Assert.Equal(fact.Id, row.Id);
        Assert.True(row.Result.GetProperty("corrected").GetBoolean());
        Assert.Null(row.StreamId);
        Assert.Null(row.FactId);
        Assert.Null(row.Source);
        Assert.Equal(kind, row.Kind);
        if (kind == "segment")
        {
            Assert.Equal(fact.End, row.End);
            fact.Revision = 4;
            fact.End = fact.Start!.Value.AddMinutes(4);
            await PostIndependent(http, fact, HttpStatusCode.OK);
            row = Assert.Single((await http.GetFromJsonAsync<List<FactResponse>>("/api/v1/users/alice/facts/segments"))!);
            Assert.Equal(4, row.Revision);
            Assert.Equal(fact.End, row.End);
        }
    }

    [Theory]
    [InlineData("collector")]
    [InlineData("foi")]
    [InlineData("aspect")]
    [InlineData("result")]
    [InlineData("relations")]
    public async Task IndependentObservation_RejectsIncompleteNativeInput(string missing)
    {
        await SeedIndependentOwner();
        await using var app = CreateApplication();
        using var http = IndependentClient(app);
        var fact = IndependentFact("segment");
        switch (missing)
        {
            case "collector": fact.CollectorId = Guid.Empty; break;
            case "foi": fact.Foi = null; break;
            case "aspect": fact.Aspect = null; break;
            case "result": fact.Result = null; break;
            case "relations": fact.Relations = null!; break;
        }
        await PostIndependent(http, fact, missing == "relations" ? HttpStatusCode.BadRequest : HttpStatusCode.UnprocessableEntity);
        Assert.Empty((await http.GetFromJsonAsync<List<FactResponse>>("/api/v1/users/alice/facts/segments"))!);
    }

    [Theory]
    [InlineData("machine", "heartbeat.device", "independent-machine")]
    [InlineData("app", "heartbeat.app", "independent-product")]
    [InlineData("app", "heartbeat.app-identity", "win:independent-product")]
    [InlineData("account", "vrchat", "usr_11111111-1111-4111-8111-111111111111")]
    [InlineData("person", "heartbeat.person", "establish")]
    public async Task IndependentObservation_AllObjectKindsNeedNoDeviceRelation(string kind, string scope, string key)
    {
        await SeedIndependentOwner();
        await using var app = CreateApplication();
        using var http = IndependentClient(app);
        // Provision via the authenticated public interface before establishing a Person.
        await http.GetAsync("/api/v1/me/person");
        if (kind == "person")
        {
            using var established = await http.PutAsync("/api/v1/me/person", null);
            established.EnsureSuccessStatusCode();
            var person = await established.Content.ReadFromJsonAsync<JsonElement>();
            key = person.GetProperty("reference").GetString()!;
        }
        var fact = IndependentFact("event");
        fact.Foi = new(kind, scope, key);
        await PostIndependent(http, fact, HttpStatusCode.OK);
        var row = Assert.Single((await http.GetFromJsonAsync<List<FactResponse>>("/api/v1/users/alice/facts/events"))!);
        Assert.Equal(kind, row.Foi!.Kind);
        Assert.Empty(row.Relations);
        Assert.Single((await http.GetFromJsonAsync<List<FactResponse>>($"/api/v1/users/alice/facts/events?foiId={row.FoiId}"))!);
        if (kind != "machine") Assert.Null(row.DeviceId);
    }


    [Theory]
    [InlineData("segment", "owner", false)]
    [InlineData("segment", "owner", true)]
    [InlineData("event", "owner", false)]
    [InlineData("event", "owner", true)]
    [InlineData("segment", "other", false)]
    [InlineData("segment", "other", true)]
    [InlineData("event", "other", false)]
    [InlineData("event", "other", true)]
    public async Task IndependentObservation_LegacyImportIdentityCollisionIsAnExplicitConflict(string kind, string uploadingOwner, bool crossKind)
    {
        await SeedIndependentOwner();
        await using var app = CreateApplication();
        using var http = IndependentClient(app);
        var savedKind = crossKind ? (kind == "segment" ? "event" : "segment") : kind;
        var fact = IndependentFact(savedKind);
        fact.Id = Guid.CreateVersion7();
        await PostIndependent(http, fact, HttpStatusCode.OK);
        http.DefaultRequestHeaders.Remove("X-Test-Owner");
        http.DefaultRequestHeaders.Add("X-Test-Owner", uploadingOwner);
        http.DefaultRequestHeaders.Add("X-Hardware-Id", "collision-import-machine");
        HttpResponseMessage collision;
        if (kind == "segment")
        {
            var item = FactStoreTests.LegacySegment(FactStoreTests.SegmentBatch());
            item.Id = fact.Id;
            collision = await http.PostAsJsonAsync("/api/v1/segments",
                new Heartbeat.Core.DTOs.Segments.SegmentUploadRequest { Segments = [item] });
        }
        else
            collision = await http.PostAsJsonAsync("/api/v1/input-events",
                new Heartbeat.Core.DTOs.Input.InputEventUploadRequest { Events = [new()
                { Id = fact.Id, Timestamp = DateTimeOffset.Parse("2026-01-01T00:00:00Z"), EventType = Heartbeat.Core.DTOs.Input.InputEventType.MouseButton,
                    CodeSet = Heartbeat.Core.DTOs.Input.InputCodeSets.HeartbeatKeyPositionV1, Code = 1 }] });
        using (collision)
            Assert.True(collision.StatusCode == HttpStatusCode.Conflict, await collision.Content.ReadAsStringAsync());
        http.DefaultRequestHeaders.Remove("X-Test-Owner");
        http.DefaultRequestHeaders.Add("X-Test-Owner", "owner");
        var row = Assert.Single((await http.GetFromJsonAsync<List<FactResponse>>($"/api/v1/users/alice/facts/{savedKind}s"))!);
        Assert.Equal(fact.Id, row.Id);
        Assert.Equal(1, row.Revision);
        Assert.True(JsonElement.DeepEquals(fact.Result!.Value, row.Result));
    }

    [Fact]
    public async Task IndependentObservation_StaleRevisionStillRequiresValidObjectReferences()
    {
        await SeedIndependentOwner();
        await using var app = CreateApplication();
        using var http = IndependentClient(app);
        var fact = IndependentFact("event");
        fact.Revision = 2;
        await PostIndependent(http, fact, HttpStatusCode.OK);
        fact.Revision = 1;
        foreach (var reference in new ObservationObjectReference[]
        {
            new("invalid-kind", "example", "account-a"), new("account", "", "account-a"),
            new("machine", "invented-scope", "machine"), new("person", ObservationObjectScopes.Person, Guid.NewGuid().ToString())
        })
        {
            fact.Foi = reference;
            await PostIndependent(http, fact, HttpStatusCode.UnprocessableEntity);
        }
        Assert.Equal(2, Assert.Single((await http.GetFromJsonAsync<List<FactResponse>>("/api/v1/users/alice/facts/events"))!).Revision);
    }

    [Theory]
    [InlineData("segment")]
    [InlineData("event")]
    public async Task IndependentObservation_NonfiniteStorageTimeIsRejectedAtHttpBoundary(string kind)
    {
        await SeedIndependentOwner();
        await using var app = CreateApplication();
        using var http = IndependentClient(app);
        var fact = IndependentFact(kind);
        if (kind == "segment") fact.Start = DateTimeOffset.MinValue;
        else fact.OccurredAt = DateTimeOffset.MinValue;
        await PostIndependent(http, fact, HttpStatusCode.UnprocessableEntity);
        Assert.Empty((await http.GetFromJsonAsync<List<FactResponse>>($"/api/v1/users/alice/facts/{kind}s"))!);
    }

    private async Task SeedIndependentOwner()
    {
        await using var db = CreateDbContext();
        db.Users.Add(new User { Id = "owner", Username = "alice" });
        await db.SaveChangesAsync();
    }

    private static ObservationSnapshot IndependentFact(string kind) => new()
    {
        Id = Guid.NewGuid(), Kind = kind, CollectorId = Guid.NewGuid(), Foi = new("account", "example", "account-a"),
        Aspect = "future-aspect", Result = JsonSerializer.SerializeToElement(new { future = new[] { 1, 2 } }), Revision = 1,
        Start = kind == "segment" ? DateTimeOffset.Parse("2026-01-01T00:00:00Z") : null,
        End = kind == "segment" ? DateTimeOffset.Parse("2026-01-01T00:02:00Z") : null,
        OccurredAt = kind == "event" ? DateTimeOffset.Parse("2026-01-01T00:00:00Z") : null
    };

    private static HttpClient IndependentClient(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Heartbeat.Server.Controllers.FactController> app)
    {
        var http = app.CreateClient();
        http.DefaultRequestHeaders.Add(HeartbeatProtocol.VersionHeader, HeartbeatProtocol.RequiredVersion);
        http.DefaultRequestHeaders.Add("X-Test-Owner", "owner");
        return http;
    }

    private static async Task PostIndependent(HttpClient http, ObservationSnapshot fact, HttpStatusCode status)
    {
        using var response = await http.PostAsJsonAsync("/api/v1/observations", new ObservationUploadRequest { Facts = [fact] });
        Assert.True(response.StatusCode == status, $"Expected {status}, got {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

}
