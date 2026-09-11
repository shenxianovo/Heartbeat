using Heartbeat.Collection.Hub.Collectors.Packages;
using Heartbeat.Collection.Hub.Collectors.Protocol;
using Heartbeat.Collection.Hub.Collectors.Runtime;
using Heartbeat.Collection.Hub.Upload;
using Heartbeat.Collector.System.Collection;
using System.Net.Http.Json;
using System.Text.Json;
using Heartbeat.Core;
using Heartbeat.Core.DTOs.Facts;
using Heartbeat.Server.Entities;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Tests.Services;

public sealed partial class FactHttpTests
{
    private static readonly DateTimeOffset PersonStart = new(2026, 9, 1, 1, 0, 0, TimeSpan.Zero);

    private async Task SeedPersonOwners(bool includeOther = false)
    {
        await using var db = CreateDbContext();
        db.Users.Add(new User { Id = "owner", Username = "alice" });
        if (includeOther) db.Users.Add(new User { Id = "other", Username = "bob" });
        await db.SaveChangesAsync();
    }

    private static async Task<JsonElement> ReadPersonPage(HttpClient http, string query = "", string family = "segments")
    {
        using var response = await http.GetAsync($"/api/v1/me/person/facts/{family}{query}");
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ObjectWithoutAccountMetadata_IsReadableDirectlyAndThroughConfirmedTernaryEvidence(bool appObservation)
    {
        await SeedPersonOwners();
        await using var app = CreateApplication();
        using var http = app.CreateClient();
        http.DefaultRequestHeaders.Add(HeartbeatProtocol.VersionHeader, HeartbeatProtocol.RequiredVersion);
        http.DefaultRequestHeaders.Add("X-Test-Owner", "owner");
        var account = new ObservationObjectReference("account", "social.example", "member-1");
        var product = new ObservationObjectReference("app", ObservationObjectScopes.App, "social-app");
        var batch = FactStoreTests.SegmentBatch("account");
        batch.Facts[0].CollectorId = batch.Streams[0].CollectorInstanceId;
        batch.Facts[0].Foi = appObservation ? product : account;
        batch.Facts[0].Aspect = "custom.snapshot";
        batch.Facts[0].Relations = appObservation ? [new("application-account-use", [new("app", product), new("account", account),
            new("device", new("machine", ObservationObjectScopes.Machine, "observed-machine"))])] : [];
        (await http.PostAsJsonAsync("/api/v1/facts", batch)).EnsureSuccessStatusCode();
        var original = Assert.Single((await http.GetFromJsonAsync<List<FactResponse>>("/api/v1/users/alice/facts/segments"))!);
        Assert.Equal(appObservation ? "app" : "account", original.Foi!.Kind);
        Assert.Equal(batch.Facts[0].CollectorId, original.CollectorId);
        (await http.PutAsJsonAsync("/api/v1/me/person", new { })).EnsureSuccessStatusCode();
        var settings = await http.GetFromJsonAsync<JsonElement>("/api/v1/me/person");
        var accountId = settings.GetProperty("objects").EnumerateArray().Single(o => o.GetProperty("kind").GetString() == "account").GetProperty("id").GetGuid();
        (await http.PostAsJsonAsync("/api/v1/me/person/associations", new { objectId = accountId, start = (DateTimeOffset?)null, end = (DateTimeOffset?)null })).EnsureSuccessStatusCode();
        var saved = Assert.Single((await ReadPersonPage(http)).GetProperty("items").EnumerateArray()).GetProperty("fact");
        Assert.Equal(original.Id, saved.GetProperty("id").GetGuid());
        Assert.Equal(original.FoiId, saved.GetProperty("foiId").GetGuid());
        await using var verify = CreateDbContext();
        Assert.Empty(await verify.ServiceAccounts.ToListAsync());
    }

    [Fact]
    public async Task PersonAssociation_BackdatesExistingDeviceFact_WithoutRewritingIt()
    {
        await SeedPersonOwners();
        await using var app = CreateApplication();
        using var http = app.CreateClient();
        http.DefaultRequestHeaders.Add(HeartbeatProtocol.VersionHeader, HeartbeatProtocol.RequiredVersion);
        http.DefaultRequestHeaders.Add("X-Test-Owner", "owner");
        var batch = FactStoreTests.SegmentBatch();
        batch.Streams[0].Source = "system";
        batch.Facts[0].ObserverId = batch.Streams[0].CollectorInstanceId;
        batch.Facts[0].Target = new FactTarget("device", batch.Streams[0].Subject.HardwareId!);
        batch.Facts[0].Start = PersonStart; batch.Facts[0].End = PersonStart.AddMinutes(10);
        using var uploaded = await http.PostAsJsonAsync("/api/v1/facts", batch);
        Assert.True(uploaded.IsSuccessStatusCode, await uploaded.Content.ReadAsStringAsync());
        var before = await http.GetStringAsync("/api/v1/users/alice/facts/segments");
        var original = JsonSerializer.Deserialize<List<FactResponse>>(before, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.Single();
        using var created = await http.PutAsJsonAsync("/api/v1/me/person", new { });
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        var empty = await ReadPersonPage(http);
        Assert.Equal(0, empty.GetProperty("totalCount").GetInt32());
        using var linked = await http.PostAsJsonAsync("/api/v1/me/person/associations", new
        {
            objectId = original.FoiId, start = PersonStart.AddMinutes(2), end = PersonStart.AddMinutes(5)
        });
        Assert.True(linked.IsSuccessStatusCode, await linked.Content.ReadAsStringAsync());
        var page = await ReadPersonPage(http);
        Assert.Equal(1, page.GetProperty("totalCount").GetInt32());
        var item = Assert.Single(page.GetProperty("items").EnumerateArray());
        Assert.Equal(original.FactId, item.GetProperty("fact").GetProperty("factId").GetGuid());
        Assert.Equal(PersonStart, item.GetProperty("fact").GetProperty("start").GetDateTimeOffset());
        Assert.Equal(PersonStart.AddMinutes(10), item.GetProperty("fact").GetProperty("end").GetDateTimeOffset());
        var interval = Assert.Single(item.GetProperty("effectiveIntervals").EnumerateArray());
        Assert.Equal(PersonStart.AddMinutes(2), interval.GetProperty("start").GetDateTimeOffset());
        Assert.Equal(PersonStart.AddMinutes(5), interval.GetProperty("end").GetDateTimeOffset());
        Assert.Equal(180, item.GetProperty("effectiveSeconds").GetDouble());
        Assert.Equal(before, await http.GetStringAsync("/api/v1/users/alice/facts/segments"));
    }
    [Fact]
    public async Task PersonalObject_IsExplicitOwnerScopedAndPreservedByReplay()
    {
        await SeedPersonOwners(true);
        await using var app = CreateApplication();
        using var http = app.CreateClient();
        http.DefaultRequestHeaders.Add(HeartbeatProtocol.VersionHeader, HeartbeatProtocol.RequiredVersion);
        http.DefaultRequestHeaders.Add("X-Test-Owner", "owner");
        using var established = await http.PutAsJsonAsync("/api/v1/me/person", new { });
        var person = await established.Content.ReadFromJsonAsync<JsonElement>();
        var batch = FactStoreTests.SegmentBatch("person");
        batch.Streams[0].Source = "personal.fixture";
        batch.Facts[0].CollectorId = batch.Streams[0].CollectorInstanceId;
        batch.Facts[0].Foi = new ObservationObjectReference("person", ObservationObjectScopes.Person, person.GetProperty("reference").GetString()!);
        batch.Facts[0].Relations = [];
        batch.Facts[0].Aspect = "personal.note";
        batch.Facts[0].Payload = JsonSerializer.SerializeToElement(new { note = "Explicit personal observation", privateDetail = "preserve" });
        using var uploaded = await http.PostAsJsonAsync("/api/v1/facts", batch);
        Assert.True(uploaded.IsSuccessStatusCode, await uploaded.Content.ReadAsStringAsync());
        var before = await http.GetStringAsync("/api/v1/users/alice/facts/segments");
        var page = await ReadPersonPage(http);
        var item = Assert.Single(page.GetProperty("items").EnumerateArray());
        Assert.Equal("person", item.GetProperty("fact").GetProperty("foi").GetProperty("kind").GetString());
        Assert.Equal(person.GetProperty("id").GetGuid(), item.GetProperty("fact").GetProperty("foiId").GetGuid());
        Assert.Equal(600, item.GetProperty("effectiveSeconds").GetDouble());
        using var replay = await http.PostAsJsonAsync("/api/v1/facts", batch);
        Assert.True(replay.IsSuccessStatusCode, await replay.Content.ReadAsStringAsync());
        Assert.Equal(before, await http.GetStringAsync("/api/v1/users/alice/facts/segments"));
        http.DefaultRequestHeaders.Remove("X-Test-Owner");
        http.DefaultRequestHeaders.Add("X-Test-Owner", "other");
        using var rejected = await http.PostAsJsonAsync("/api/v1/facts", batch);
        Assert.Equal(System.Net.HttpStatusCode.UnprocessableEntity, rejected.StatusCode);
        Assert.Equal(0, (await ReadPersonPage(http)).GetProperty("totalCount").GetInt32());
        Assert.Empty((await http.GetFromJsonAsync<List<FactResponse>>("/api/v1/users/bob/facts/segments"))!);
    }

    [Fact]
    public async Task PersonHistory_UnionsCoverageAcrossSources_PagesFacts_AndCorrectionAndRemovalOnlyChangeProjection()
    {
        await SeedPersonOwners();
        await using var app = CreateApplication();
        using var http = app.CreateClient();
        http.DefaultRequestHeaders.Add(HeartbeatProtocol.VersionHeader, HeartbeatProtocol.RequiredVersion);
        http.DefaultRequestHeaders.Add("X-Test-Owner", "owner");
        using var established = await http.PutAsJsonAsync("/api/v1/me/person", new { });
        var person = await established.Content.ReadFromJsonAsync<JsonElement>();
        var machine = new ObservationObjectReference("machine", ObservationObjectScopes.Machine, "history-device");
        var product = new ObservationObjectReference("app", ObservationObjectScopes.AppIdentity, "mac:com.google.chrome");
        var objects = new[] { machine, product,
            new ObservationObjectReference("account", "vrchat", "usr_11111111-1111-4111-8111-111111111111"),
            new ObservationObjectReference("person", ObservationObjectScopes.Person, person.GetProperty("reference").GetString()!) };
        var sources = new[] { "system", "browser", "vrchat.account", "personal.fixture" };
        for (var n = 0; n < objects.Length; n++)
        {
            var batch = FactStoreTests.SegmentBatch("person");
            batch.Streams[0].Source = sources[n];
            batch.Facts[0].CollectorId = batch.Streams[0].CollectorInstanceId;
            batch.Facts[0].Foi = objects[n];
            batch.Facts[0].Aspect = "personal.fixture";
            batch.Facts[0].Relations = n == 1 ? [new FactRelationSnapshot("observed-on", [new("device", machine), new("app", product)])] : [];
            batch.Facts[0].Payload = JsonSerializer.SerializeToElement(new { activityKey = sources[n], title = "Stored before association" });
            using var upload = await http.PostAsJsonAsync("/api/v1/facts", batch);
            Assert.True(upload.IsSuccessStatusCode, await upload.Content.ReadAsStringAsync());
        }
        var before = await http.GetStringAsync("/api/v1/users/alice/facts/segments");
        var facts = JsonSerializer.Deserialize<List<FactResponse>>(before, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(1, (await ReadPersonPage(http)).GetProperty("totalCount").GetInt32());
        var deviceId = facts.Single(f => f.Foi!.Kind == "machine").FoiId!.Value;
        var accountId = facts.Single(f => f.Foi!.Kind == "account").FoiId!.Value;
        async Task<Guid> Link(Guid objectId, int start, int end)
        {
            using var response = await http.PostAsJsonAsync("/api/v1/me/person/associations", new
            { objectId, start = PersonStart.AddMinutes(start), end = PersonStart.AddMinutes(end) });
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        }
        var first = await Link(deviceId, 2, 5);
        var overlap = await Link(deviceId, 4, 6);
        var later = await Link(deviceId, 8, 9);
        var accountLink = await Link(accountId, 3, 7);
        var page = await ReadPersonPage(http, "?limit=2");
        Assert.Equal(4, page.GetProperty("totalCount").GetInt32());
        Assert.Equal(4, page.GetProperty("sources").GetArrayLength());
        Assert.All(page.GetProperty("sources").EnumerateArray(), source => Assert.Equal(1, source.GetProperty("count").GetInt32()));
        var next = await ReadPersonPage(http, "?limit=2&offset=2");
        var items = page.GetProperty("items").EnumerateArray().Concat(next.GetProperty("items").EnumerateArray()).ToArray();
        Assert.Equal(4, items.Select(item => item.GetProperty("fact").GetProperty("id").GetGuid()).Distinct().Count());
        Assert.Equal("app", items.Single(item => item.GetProperty("fact").GetProperty("source").GetString() == "browser").GetProperty("fact").GetProperty("foi").GetProperty("kind").GetString());
        Assert.Equal(facts.Select(f => f.Id), items.Select(item => item.GetProperty("fact").GetProperty("id").GetGuid()));
        foreach (var item in items.Where(item => item.GetProperty("fact").GetProperty("source").GetString() is "system" or "browser"))
        {
            Assert.Equal(300, item.GetProperty("effectiveSeconds").GetDouble());
            var ranges = item.GetProperty("effectiveIntervals").EnumerateArray().ToArray();
            Assert.Equal(2, ranges.Length);
            Assert.Equal(PersonStart.AddMinutes(2), ranges[0].GetProperty("start").GetDateTimeOffset());
            Assert.Equal(PersonStart.AddMinutes(6), ranges[0].GetProperty("end").GetDateTimeOffset());
            Assert.Equal(PersonStart.AddMinutes(8), ranges[1].GetProperty("start").GetDateTimeOffset());
            Assert.Equal(PersonStart.AddMinutes(9), ranges[1].GetProperty("end").GetDateTimeOffset());
        }
        var gap = await ReadPersonPage(http, "?start=2026-09-01T01:07:00Z&end=2026-09-01T01:08:00Z");
        Assert.Equal(1, gap.GetProperty("totalCount").GetInt32()); // Only the direct personal fact.
        using var corrected = await http.PutAsJsonAsync($"/api/v1/me/person/associations/{first}", new
        { objectId = deviceId, start = PersonStart, end = PersonStart.AddMinutes(1) });
        Assert.True(corrected.IsSuccessStatusCode, await corrected.Content.ReadAsStringAsync());
        foreach (var id in new[] { overlap, later, accountLink })
        {
            using var deleted = await http.DeleteAsync($"/api/v1/me/person/associations/{id}");
            Assert.True(deleted.IsSuccessStatusCode, await deleted.Content.ReadAsStringAsync());
        }
        var changed = await ReadPersonPage(http);
        Assert.Equal(3, changed.GetProperty("totalCount").GetInt32());
        Assert.All(changed.GetProperty("items").EnumerateArray().Where(item => item.GetProperty("fact").GetProperty("foi").GetProperty("kind").GetString() != "person"),
            item => Assert.Equal(60, item.GetProperty("effectiveSeconds").GetDouble()));
        using var removed = await http.DeleteAsync($"/api/v1/me/person/associations/{first}");
        Assert.True(removed.IsSuccessStatusCode);
        Assert.Equal(1, (await ReadPersonPage(http)).GetProperty("totalCount").GetInt32());
        Assert.Equal(before, await http.GetStringAsync("/api/v1/users/alice/facts/segments"));
    }

    [Theory]
    [InlineData("machine")]
    [InlineData("app")]
    [InlineData("account")]
    [InlineData("person")]
    public async Task PersonEvents_UseHalfOpenInstantsAndNeverDuplicateOverlappingMatches(string kind)
    {
        await SeedPersonOwners();
        await using var app = CreateApplication();
        using var http = app.CreateClient();
        http.DefaultRequestHeaders.Add(HeartbeatProtocol.VersionHeader, HeartbeatProtocol.RequiredVersion);
        http.DefaultRequestHeaders.Add("X-Test-Owner", "owner");
        using var established = await http.PutAsJsonAsync("/api/v1/me/person", new { });
        var person = await established.Content.ReadFromJsonAsync<JsonElement>();
        var batch = FactStoreTests.SegmentBatch("person");
        batch.Streams[0].Source = kind == "account" ? "vrchat.account" : kind == "app" ? "browser" : kind == "machine" ? "system" : "personal.fixture";
        batch.Streams[0].FactKind = "event";
        var machine = new ObservationObjectReference("machine", ObservationObjectScopes.Machine, "event-history");
        var foi = kind switch
        {
            "account" => new ObservationObjectReference("account", "vrchat", "usr_11111111-1111-4111-8111-111111111111"),
            "app" => new ObservationObjectReference("app", ObservationObjectScopes.AppIdentity, "mac:com.google.chrome"),
            "person" => new ObservationObjectReference("person", ObservationObjectScopes.Person, person.GetProperty("reference").GetString()!),
            _ => machine
        };
        batch.Facts = new[] { 1, 2, 4, 5, 6, 8, 9 }.Select(minute => new FactSnapshot
        {
            StreamId = batch.Streams[0].StreamId, FactId = Guid.CreateVersion7(), Revision = 1,
            CollectorId = batch.Streams[0].CollectorInstanceId, Foi = foi, Aspect = "personal.fixture",
            Relations = kind == "app" ? [new FactRelationSnapshot("observed-on", [new("device", machine), new("app", foi)])] : [],
            OccurredAt = PersonStart.AddMinutes(minute),
            Payload = JsonSerializer.SerializeToElement(new { minute, evidence = "do not modify" })
        }).ToList();
        using var uploaded = await http.PostAsJsonAsync("/api/v1/facts", batch);
        Assert.True(uploaded.IsSuccessStatusCode, await uploaded.Content.ReadAsStringAsync());
        var before = await http.GetStringAsync("/api/v1/users/alice/facts/events");
        var original = JsonSerializer.Deserialize<List<FactResponse>>(before, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.First();
        var ids = new List<Guid>();
        if (kind != "person")
            foreach (var (lower, upper) in new[] { (2, 5), (4, 6), (8, 9) })
            {
                using var linked = await http.PostAsJsonAsync("/api/v1/me/person/associations", new
                {
                    objectId = kind == "app" ? original.Relations.Single().Members.Single(m => m.Role == "device").Object.Id : original.FoiId,
                    start = PersonStart.AddMinutes(lower), end = PersonStart.AddMinutes(upper)
                });
                Assert.True(linked.IsSuccessStatusCode, await linked.Content.ReadAsStringAsync());
                ids.Add((await linked.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());
            }
        var page = await ReadPersonPage(http, family: "events");
        var expected = kind == "person" ? new[] { 9, 8, 6, 5, 4, 2, 1 } : new[] { 8, 5, 4, 2 };
        Assert.Equal(expected, page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("fact").GetProperty("payload").GetProperty("minute").GetInt32()));
        Assert.Equal(expected.Length, page.GetProperty("totalCount").GetInt32());
        Assert.Equal(expected.Length, Assert.Single(page.GetProperty("sources").EnumerateArray()).GetProperty("count").GetInt32());
        Assert.All(page.GetProperty("items").EnumerateArray(), i =>
        {
            Assert.Empty(i.GetProperty("effectiveIntervals").EnumerateArray());
            Assert.Equal(JsonValueKind.Null, i.GetProperty("effectiveSeconds").ValueKind);
        });
        var window = await ReadPersonPage(http, "?start=2026-09-01T01:04:00Z&end=2026-09-01T01:05:00Z", "events");
        Assert.Equal(4, Assert.Single(window.GetProperty("items").EnumerateArray()).GetProperty("fact").GetProperty("payload").GetProperty("minute").GetInt32());
        foreach (var id in ids) (await http.DeleteAsync($"/api/v1/me/person/associations/{id}")).EnsureSuccessStatusCode();
        Assert.Equal(kind == "person" ? 7 : 0, (await ReadPersonPage(http, family: "events")).GetProperty("totalCount").GetInt32());
        Assert.Equal(before, await http.GetStringAsync("/api/v1/users/alice/facts/events"));
    }

    [Fact]
    public async Task PersonManagement_ListsOfflineAndUnknownObjects_RejectsCrossOwnerAndInvalidRanges()
    {
        await SeedPersonOwners(true);
        await using var app = CreateApplication();
        using var http = app.CreateClient();
        http.DefaultRequestHeaders.Add(HeartbeatProtocol.VersionHeader, HeartbeatProtocol.RequiredVersion);
        http.DefaultRequestHeaders.Add("X-Test-Owner", "owner");
        using var established = await http.PutAsJsonAsync("/api/v1/me/person", new { });
        var personal = await established.Content.ReadAsStringAsync();
        using var again = await http.PutAsJsonAsync("/api/v1/me/person", new { });
        Assert.Equal(personal, await again.Content.ReadAsStringAsync());
        var batch = FactStoreTests.SegmentBatch();
        batch.Streams[0].Source = "system";
        batch.Facts[0].ObserverId = batch.Streams[0].CollectorInstanceId;
        batch.Streams[0].Subject.HardwareId = "offline-history";
        batch.Facts[0].Target = new FactTarget("device", "offline-history");
        (await http.PostAsJsonAsync("/api/v1/facts", batch)).EnsureSuccessStatusCode();
        var unknown = ServiceAccountTests.Batch();
        unknown.Facts[0].ObserverId = null; unknown.Facts[0].Target = null;
        (await http.PostAsJsonAsync("/api/v1/facts", unknown)).EnsureSuccessStatusCode();
        using var settingsResponse = await http.GetAsync("/api/v1/me/person");
        Assert.True(settingsResponse.IsSuccessStatusCode, await settingsResponse.Content.ReadAsStringAsync());
        var settings = await settingsResponse.Content.ReadFromJsonAsync<JsonElement>();
        var targets = settings.GetProperty("objects").EnumerateArray().ToArray();
        var deviceId = targets.Single(t => t.GetProperty("kind").GetString() == "machine").GetProperty("id").GetGuid();
        var account = targets.Single(t => t.GetProperty("kind").GetString() == "account");
        Assert.Equal(JsonValueKind.Null, account.GetProperty("name").ValueKind);
        Assert.False(string.IsNullOrWhiteSpace(account.GetProperty("key").GetString()));
        Assert.Empty(settings.GetProperty("associations").EnumerateArray());
        using var linked = await http.PostAsJsonAsync("/api/v1/me/person/associations", new { objectId = deviceId, start = (DateTimeOffset?)null, end = PersonStart.AddMinutes(5) });
        Assert.True(linked.IsSuccessStatusCode, await linked.Content.ReadAsStringAsync());
        var linkId = (await linked.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(300, Assert.Single((await ReadPersonPage(http)).GetProperty("items").EnumerateArray()).GetProperty("effectiveSeconds").GetDouble());
        using var openEnd = await http.PutAsJsonAsync($"/api/v1/me/person/associations/{linkId}", new { objectId = deviceId, start = PersonStart.AddMinutes(8), end = (DateTimeOffset?)null });
        openEnd.EnsureSuccessStatusCode();
        Assert.Equal(120, Assert.Single((await ReadPersonPage(http)).GetProperty("items").EnumerateArray()).GetProperty("effectiveSeconds").GetDouble());
        using var unknownLink = await http.PostAsJsonAsync("/api/v1/me/person/associations", new { objectId = account.GetProperty("id").GetGuid(), start = (DateTimeOffset?)null, end = (DateTimeOffset?)null });
        unknownLink.EnsureSuccessStatusCode();
        Assert.Equal(2, (await ReadPersonPage(http)).GetProperty("totalCount").GetInt32());
        foreach (var body in new object[]
        {
            new { objectId = deviceId, start = PersonStart, end = PersonStart },
            new { objectId = deviceId, start = PersonStart.AddMinutes(1), end = PersonStart },
            new { objectId = deviceId, accountId = account.GetProperty("id").GetGuid(), start = PersonStart, end = PersonStart.AddMinutes(1) },
            new { objectId = deviceId, start = PersonStart.AddTicks(1), end = PersonStart.AddMinutes(1) },
            new { objectId = deviceId, ownerId = "other", start = PersonStart, end = PersonStart.AddMinutes(1) },
            new { objectId = deviceId }
        })
        {
            using var invalid = await http.PostAsJsonAsync("/api/v1/me/person/associations", body);
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        http.DefaultRequestHeaders.Remove("X-Test-Owner");
        http.DefaultRequestHeaders.Add("X-Test-Owner", "other");
        (await http.PutAsJsonAsync("/api/v1/me/person", new { })).EnsureSuccessStatusCode();
        var otherSettings = await http.GetFromJsonAsync<JsonElement>("/api/v1/me/person");
        Assert.Empty(otherSettings.GetProperty("objects").EnumerateArray());
        Assert.Empty(otherSettings.GetProperty("associations").EnumerateArray());
        using var crossCreate = await http.PostAsJsonAsync("/api/v1/me/person/associations", new { objectId = deviceId, start = PersonStart, end = PersonStart.AddMinutes(1) });
        using var crossCorrect = await http.PutAsJsonAsync($"/api/v1/me/person/associations/{linkId}", new { objectId = deviceId, start = PersonStart, end = PersonStart.AddMinutes(1) });
        using var crossRemove = await http.DeleteAsync($"/api/v1/me/person/associations/{linkId}");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, crossCreate.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, crossCorrect.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, crossRemove.StatusCode);
        using var privateFacts = await http.GetAsync("/api/v1/users/alice/facts/segments");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, privateFacts.StatusCode);
        Assert.Equal(0, (await ReadPersonPage(http)).GetProperty("totalCount").GetInt32());
        http.DefaultRequestHeaders.Remove("X-Test-Owner");
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, (await http.GetAsync("/api/v1/me/person")).StatusCode);
    }

    [Theory]
    [InlineData("segment")]
    [InlineData("event")]
    public async Task PersonalObject_RuntimeCustodyAndRestartReachThePublicPersonView(string kind)
    {
        await SeedPersonOwners();
        await using var app = CreateApplication();
        using var http = app.CreateClient();
        http.DefaultRequestHeaders.Add(HeartbeatProtocol.VersionHeader, HeartbeatProtocol.RequiredVersion);
        http.DefaultRequestHeaders.Add("X-Test-Owner", "owner");
        using var established = await http.PutAsJsonAsync("/api/v1/me/person", new { });
        var person = await established.Content.ReadFromJsonAsync<JsonElement>();
        var directory = Path.Combine(Path.GetTempPath(), $"heartbeat-person-http-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var package = LocalCollectorPackage.Load(SystemCollectorPackage.Path);
            var path = Path.Combine(directory, "runtime.json");
            var options = new CollectorRuntimeOptions();
            Guid observer;
            Guid factId;
            using (var runtime = CollectorRuntime.Open(path, new UnusedProjection(), options))
            {
                observer = runtime.CreateInstance(package, new SubjectReference(Guid.NewGuid(), SubjectKind.Machine),
                    new CollectorInstanceSpec(1, 1, JsonSerializer.SerializeToElement(new { }))).CollectorInstanceId;
                await using var activation = await runtime.ActivateInProcessAsync(observer, package, new PayloadCollector(package));
                var writer = activation.Streams[kind == "segment" ? "foreground" : "input-events"];
                factId = Guid.CreateVersion7();
                var fact = new FactSubmission(writer.Descriptor.StreamId, factId, 1, null,
                    kind == "segment" ? new SegmentFactTime(PersonStart, PersonStart.AddMinutes(10), true) : new EventFactTime(PersonStart),
                    JsonSerializer.SerializeToElement(new { note = "An explicitly personal fixture" }),
                    observer, new ObservationObjectReference("person", ObservationObjectScopes.Person, person.GetProperty("reference").GetString()!), "personal.note", []);
                Assert.Equal(FactDeliveryStatus.Committed, Assert.Single((await writer.PublishAsync(Guid.CreateVersion7(), [fact])).Results).Status);
            }
            using var restarted = CollectorRuntime.Open(path, new UnusedProjection(), options);
            var pending = restarted.ReadPendingFacts();
            Assert.Single(pending);
            using var posted = await http.PostAsJsonAsync("/api/v1/facts", FactUploadItem.Request(pending));
            Assert.True(posted.IsSuccessStatusCode, await posted.Content.ReadAsStringAsync());
            var page = await ReadPersonPage(http, family: kind + "s");
            var saved = Assert.Single(page.GetProperty("items").EnumerateArray()).GetProperty("fact");
            Assert.Equal(observer, saved.GetProperty("observerId").GetGuid());
            Assert.Equal(factId, saved.GetProperty("factId").GetGuid());
            Assert.Equal("person", saved.GetProperty("foi").GetProperty("kind").GetString());
            using var replay = await http.PostAsJsonAsync("/api/v1/facts", FactUploadItem.Request(pending));
            Assert.True(replay.IsSuccessStatusCode);
            restarted.ConfirmUploadedFacts(pending);
            Assert.Empty(restarted.ReadPendingFacts());
            Assert.Equal(1, (await ReadPersonPage(http, family: kind + "s")).GetProperty("totalCount").GetInt32());
        }
        finally { Directory.Delete(directory, true); }
    }

}
