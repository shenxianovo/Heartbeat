using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Heartbeat.Core;
using Heartbeat.Observer.Sdk;
using Heartbeat.Observers.ForegroundState;

namespace Heartbeat.Integration.Tests;

public sealed class ObserverSdkTests
{
    [Test]
    public async Task DraftMergesFreezeDependenciesAndRecoverInOrder()
    {
        using var directory = new SdkDirectory();
        using var handler = new ScriptedBackend();
        using var http = new HttpClient(handler) { BaseAddress = new("http://localhost/") };
        var owner = EntityId.New();
        var schema = EntityApiTests.Schema();
        var observationId = EntityId.New();
        VersionId original;
        using (var sdk = new ObserverClient(directory.Path, owner, http))
        {
            sdk.Execute(local =>
            {
                local.Remember(new(schema, DateTimeOffset.UtcNow));
                var content = EntityApiTests.Business(schema);
                local.Draft(content.EntityId, content.Content, schema.VersionId, SubmissionStrategy.OnCompletion);
                var referenced = local.Reference(content.EntityId);
                original = local.Draft(observationId, new Observation(referenced, referenced, null, null, null, null), strategy: SubmissionStrategy.OnCompletion);
            });
            original = sdk.Execute(local => local.Latest(observationId)!.Version.VersionId);
            sdk.Execute(local => local.Draft(observationId, ((Observation)local.Find(original)!.Version.Content) with { TimeZone = "Asia/Shanghai" }, strategy: SubmissionStrategy.OnCompletion));
            await Assert.That(sdk.Execute(local => local.Latest(observationId)!.Version.VersionId)).IsEqualTo(original);
            await sdk.SendAsync();
            await Assert.That(sdk.Status.Drafts).IsEqualTo(1);
            await Assert.That(handler.Posts.Single().Length).IsEqualTo(1);
        }
        using var restarted = new ObserverClient(directory.Path, owner, http);
        restarted.Execute(local => local.Complete(observationId));
        await restarted.SendAsync();
        await Assert.That(handler.Posts[^1].Single().VersionId).IsEqualTo(original);
        await Assert.That(restarted.Status.Pending).IsEqualTo(0);
        await Assert.That(restarted.Execute(local => local.Latest(observationId)!.Version.VersionId)).IsEqualTo(original);
    }

    [Test]
    public async Task LostResponseRetriesIdenticalPayloadAfterRestartAnd413Splits()
    {
        using var directory = new SdkDirectory();
        using var handler = new ScriptedBackend { LoseFirstResponse = true, MaximumBatch = 1 };
        using var http = new HttpClient(handler) { BaseAddress = new("http://localhost/") };
        var owner = EntityId.New();
        using (var sdk = new ObserverClient(directory.Path, owner, http))
        {
            sdk.Execute(local =>
            {
                local.Draft(EntityId.New(), EntityApiTests.Schema("one").Content);
                local.Draft(EntityId.New(), EntityApiTests.Schema("two").Content);
            });
            await sdk.SendAsync();
            await Assert.That(sdk.Status.Pending).IsEqualTo(2);
        }
        using var restarted = new ObserverClient(directory.Path, owner, http);
        await restarted.SendAsync();
        await Assert.That(JsonSerializer.Serialize(handler.Posts[0].Select(VersionJson.Submission)))
            .IsEqualTo(JsonSerializer.Serialize(handler.Posts[1].Select(VersionJson.Submission)));
        await Assert.That(handler.Posts.Skip(2).All(batch => batch.Length == 1)).IsTrue();
        await Assert.That(restarted.Status.Pending).IsEqualTo(0);
        await Assert.That(restarted.Status.Saved).IsEqualTo(2);
    }

    [Test]
    public async Task PartialRejectionPausesPersistsAndAdoptsEqualSavedVersion()
    {
        using var directory = new SdkDirectory();
        using var handler = new ScriptedBackend { RejectIndex = 1 };
        using var http = new HttpClient(handler) { BaseAddress = new("http://localhost/") };
        var owner = EntityId.New();
        var schema = EntityApiTests.Schema();
        VersionSubmission rejected;
        using (var sdk = new ObserverClient(directory.Path, owner, http))
        {
            rejected = sdk.Execute(local =>
            {
                var schemaId = local.Draft(schema.EntityId, schema.Content);
                var business = EntityApiTests.Business(schema);
                var target = local.Draft(business.EntityId, business.Content, schemaId);
                local.Draft(EntityId.New(), new InputSet([target]));
                return local.Find(target)!.Version;
            });
            await sdk.SendAsync();
            await Assert.That(sdk.Status.Saved).IsEqualTo(1);
            await Assert.That(sdk.Status.Rejection!.Dependents.Count).IsEqualTo(1);
            await sdk.SendAsync();
            await Assert.That(handler.Posts.Count).IsEqualTo(1);
        }
        using var restarted = new ObserverClient(directory.Path, owner, http);
        var adopted = new EntityVersion(rejected with { VersionId = VersionId.New() }, DateTimeOffset.UtcNow);
        restarted.Execute(local =>
        {
            var dependent = local.Rejection()!.Dependents.Single();
            local.RewriteDependent(dependent.Version.VersionId, new InputSet([rejected.VersionId, rejected.SchemaVersionId!.Value]));
            local.AdoptSaved(adopted);
        });
        handler.RejectIndex = null;
        await restarted.SendAsync();
        await Assert.That(((InputSet)handler.Posts[^1].Single().Content).Members[0]).IsEqualTo(adopted.VersionId);
        await Assert.That(restarted.Status.Rejection).IsNull();
        await Assert.That(restarted.Execute(local => local.Reference(rejected.EntityId))).IsEqualTo(adopted.VersionId);
    }

    [Test]
    public async Task SchemaRegistrationReusesHistoricalContentAndOfflineDraftReferencesAreRemapped()
    {
        await using var database = await DatabaseFixture.CreateAsync();
        await using var factory = new HeartbeatApiFactory(database.ConnectionString);
        using var http = factory.CreateClient();
        var first = EntityApiTests.Schema();
        var second = first with { VersionId = VersionId.New(), PreviousVersionId = first.VersionId, Content = new EntitySchema("New", "New description", "items", JsonSerializer.Deserialize<JsonElement>("{}")) };
        using var saved = await EntityApiTests.Submit(http, first, second);
        using var directory = new SdkDirectory();
        using var offlineHandler = new ScriptedBackend { Offline = true };
        using var offline = new HttpClient(offlineHandler) { BaseAddress = new("http://localhost/") };
        var owner = EntityId.New(); var business = EntityId.New();
        using (var sdk = new ObserverClient(directory.Path, owner, offline))
        {
            var schema = await sdk.RegisterSchemaAsync(first.EntityId, (EntitySchema)first.Content);
            sdk.Execute(local => local.Draft(business, new BusinessContent(JsonSerializer.Deserialize<JsonElement>("{}")), schema));
            await sdk.SendAsync();
        }
        using var online = new ObserverClient(directory.Path, owner, http);
        await online.SendAsync();
        await Assert.That(online.Status.Error).IsNull();
        await Assert.That(online.Status.Pending).IsEqualTo(0);
        using var read = await http.GetAsync($"/entities/{business.Value}");
        await Assert.That((await EntityApiTests.Json(read)).GetProperty("schemaVersionId").GetGuid()).IsEqualTo(first.VersionId.Value);
        var selected = await online.RegisterSchemaAsync(first.EntityId, (EntitySchema)first.Content);
        await Assert.That(selected).IsEqualTo(first.VersionId);
        using var chain = await http.GetAsync($"/entities/{first.EntityId.Value}/versions");
        await Assert.That((await EntityApiTests.Json(chain)).GetProperty("versions").GetArrayLength()).IsEqualTo(2);
    }

    [Test]
    public async Task ForegroundUploadsFinishedPeriodsAndRecoversDeletedApplication()
    {
        await using var database = await DatabaseFixture.CreateAsync();
        await using var factory = new HeartbeatApiFactory(database.ConnectionString);
        using var http = factory.CreateClient();
        using var directory = new SdkDirectory();
        var owner = EntityId.New();
        using var sdk = new ObserverClient(directory.Path, owner, http);
        var registration = await ForegroundRegistration.RegisterAsync(sdk, owner);
        var registry = new LocalApplicationRegistry(directory.Path + ".apps");
        var application = registry.Resolve(ForegroundTimelineTests.Reading(0));
        var store = new ForegroundStore(sdk, owner, registration);
        var delivery = new ForegroundDelivery(sdk, registry, registration, TextWriter.Null);
        var capture = ForegroundCapture.Create(owner, application, ForegroundTimelineTests.Reading(0));
        store.Checkpoint(capture, null);
        await delivery.SendAsync(CancellationToken.None);
        using var active = await http.GetAsync($"/entities/{capture.ObservationId.Value}");
        await Assert.That(active.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        store.FinishActive();
        await delivery.SendAsync(CancellationToken.None);
        using var ended = await http.GetAsync($"/entities/{capture.ObservationId.Value}");
        await Assert.That(ended.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var deleted = await http.DeleteAsync($"/entities/{application.Id.Value}");
        var next = ForegroundCapture.Create(owner, application, ForegroundTimelineTests.Reading(1));
        store.Checkpoint(next, null); store.FinishActive();
        await delivery.SendAsync(CancellationToken.None);
        await Assert.That(sdk.Status.Rejection).IsNull();
        await Assert.That(sdk.Status.Error).IsNull();
        await Assert.That(sdk.Status.Pending).IsEqualTo(0);
        var newApplication = registry.Resolve(ForegroundTimelineTests.Reading(2));
        await Assert.That(newApplication.Id == application.Id).IsFalse();
        using var newPeriod = await http.GetAsync($"/entities/{next.Content.Id.Value}");
        var version = (await EntityApiTests.Json(newPeriod)).GetProperty("content").GetProperty("localApplicationVersionId").GetGuid();
        using var target = await http.GetAsync($"/versions/{version}");
        await Assert.That((await EntityApiTests.Json(target)).GetProperty("entityId").GetGuid()).IsEqualTo(newApplication.Id.Value);
    }

    private sealed class SdkDirectory : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("heartbeat-sdk-tests-");
        internal string Path => System.IO.Path.Combine(_directory.FullName, "state.sqlite");
        public void Dispose() => _directory.Delete(true);
    }

    private sealed class ScriptedBackend : HttpMessageHandler
    {
        internal bool Offline { get; set; }
        internal bool LoseFirstResponse { get; init; }
        internal int? RejectIndex { get; set; }
        internal int MaximumBatch { get; set; } = int.MaxValue;
        internal List<VersionSubmission[]> Posts { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Offline) throw new HttpRequestException("offline");
            if (request.Method == HttpMethod.Get) return new(HttpStatusCode.NotFound);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var versions = json.RootElement.GetProperty("versions").EnumerateArray().Select(VersionJson.ReadSubmission).ToArray();
            Posts.Add(versions);
            if (LoseFirstResponse && Posts.Count == 1) throw new HttpRequestException("response lost");
            if (versions.Length > MaximumBatch) return new(HttpStatusCode.RequestEntityTooLarge);
            if (RejectIndex is { } index) return new(HttpStatusCode.Conflict)
            {
                Content = JsonContent.Create(new { index, versionId = versions[index].VersionId.Value, rule = "VERSION-6", detail = "stale version" }),
            };
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { versions = versions.Select(version => new { versionId = version.VersionId.Value, recordedAt = "2026-10-25T00:00:00.000000Z" }) }) };
        }
    }
}
