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
    [Property("caseId", "sdk-local-versions")]
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
            await Assert.That(sdk.Execute(local => local.Latest(observationId)!.Version.PreviousVersionId)).IsNull();
            await sdk.SendAsync();
            await Assert.That(sdk.Status.Drafts).IsEqualTo(1);
            await Assert.That(handler.Posts.Single().Length).IsEqualTo(1);
        }
        using var restarted = new ObserverClient(directory.Path, owner, http);
        restarted.Execute(local => local.Complete(observationId));
        await restarted.SendAsync();
        await Assert.That(handler.Posts[^1].Single().VersionId).IsEqualTo(original);
        await Assert.That(((Observation)handler.Posts[^1].Single().Content).TimeZone).IsEqualTo("Asia/Shanghai");
        await Assert.That(handler.Posts.SelectMany(batch => batch).Count(version => version.EntityId == observationId)).IsEqualTo(1);
        await Assert.That(restarted.Status.Pending).IsEqualTo(0);
        await Assert.That(restarted.Execute(local => local.Latest(observationId)!.Version.VersionId)).IsEqualTo(original);
    }

    [Test]
    [Property("caseId", "sdk-confirmation")]
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
    [Property("caseId", "sdk-rejections")]
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
            await Assert.That(sdk.Status.Rejection!.Version.Version.VersionId).IsEqualTo(rejected.VersionId);
            await Assert.That(sdk.Status.Rejection.Version.Rule).IsEqualTo("VERSION-6");
            await Assert.That(((InputSet)sdk.Status.Rejection.Dependents.Single().Version.Content).Members.Single()).IsEqualTo(rejected.VersionId);
            await sdk.SendAsync();
            await Assert.That(handler.Posts.Count).IsEqualTo(1);
        }
        using var restarted = new ObserverClient(directory.Path, owner, http);
        await Assert.That(restarted.Status.Rejection!.Version.Version.VersionId).IsEqualTo(rejected.VersionId);
        var adopted = new EntityVersion(rejected with { VersionId = VersionId.New() }, DateTimeOffset.UtcNow);
        restarted.Execute(local =>
        {
            local.AdoptSaved(adopted);
        });
        handler.RejectIndex = null;
        await restarted.SendAsync();
        await Assert.That(((InputSet)handler.Posts[^1].Single().Content).Members[0]).IsEqualTo(adopted.VersionId);
        await Assert.That(restarted.Status.Rejection).IsNull();
        await Assert.That(restarted.Execute(local => local.Reference(rejected.EntityId))).IsEqualTo(adopted.VersionId);
    }

    [Test]
    [Property("caseId", "sdk-registration")]
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
    [Property("caseId", "foreground-submission")]
    public async Task ForegroundUploadsOnlyFinishedPeriods()
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
        store.Checkpoint(capture.Through(ForegroundTimelineTests.Reading(1).ObservedAt), null);
        await delivery.SendAsync(CancellationToken.None);
        using var active = await http.GetAsync($"/entities/{capture.ObservationId.Value}");
        await Assert.That(active.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        store.FinishActive();
        await delivery.SendAsync(CancellationToken.None);
        using var ended = await http.GetAsync($"/entities/{capture.ObservationId.Value}");
        await Assert.That(ended.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var content = await http.GetAsync($"/entities/{capture.Content.Id.Value}");
        await Assert.That(content.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    [Property("caseId", "foreground-rejections")]
    public async Task ForegroundRecoversDeletedApplicationAndUpdatesRegistry()
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
        store.FinishActive();
        await delivery.SendAsync(CancellationToken.None);
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
        using var observation = await http.GetAsync($"/entities/{next.ObservationId.Value}");
        await Assert.That(observation.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var reopened = new LocalApplicationRegistry(directory.Path + ".apps");
        await Assert.That(reopened.Resolve(ForegroundTimelineTests.Reading(2)).Id).IsEqualTo(newApplication.Id);
    }

    [Test]
    [Property("caseId", "sdk-local-versions")]
    public async Task ReferencingDraftFreezesItAndCompletionCreatesSuccessor()
    {
        using var directory = new SdkDirectory();
        using var handler = new ScriptedBackend();
        using var http = new HttpClient(handler) { BaseAddress = new("http://localhost/") };
        using var sdk = new ObserverClient(directory.Path, EntityId.New(), http);
        var schema = EntityApiTests.Schema(fields: """{"name":{"type":"string","required":true,"nullable":false}}""");
        var entity = EntityId.New();
        var original = sdk.Execute(local =>
        {
            local.Remember(new(schema, DateTimeOffset.UtcNow));
            return local.Draft(entity, new BusinessContent(JsonSerializer.SerializeToElement(new { name = "initial" })), schema.VersionId, SubmissionStrategy.OnCompletion);
        });
        var reference = sdk.Execute(local => local.Reference(entity));
        await Assert.That(reference).IsEqualTo(original);
        await Assert.That(sdk.Execute(local => local.Find(reference)!.State)).IsEqualTo(SubmissionState.Frozen);
        var next = sdk.Execute(local => local.Draft(entity, new BusinessContent(JsonSerializer.SerializeToElement(new { name = "completed" })), schema.VersionId, SubmissionStrategy.OnCompletion));
        await Assert.That(next).IsNotEqualTo(reference);
        await Assert.That(sdk.Execute(local => local.Find(next)!.Version.PreviousVersionId)).IsEqualTo(reference);
    }

    [Test]
    [Property("caseId", "sdk-local-versions")]
    public async Task FreezingObservationSendsItsDraftContentFirst()
    {
        using var directory = new SdkDirectory();
        using var handler = new ScriptedBackend();
        using var http = new HttpClient(handler) { BaseAddress = new("http://localhost/") };
        using var sdk = new ObserverClient(directory.Path, EntityId.New(), http);
        var schema = EntityApiTests.Schema();
        var contentEntity = EntityId.New();
        var observationEntity = EntityId.New();
        var content = sdk.Execute(local =>
        {
            local.Remember(new(schema, DateTimeOffset.UtcNow));
            return local.Draft(contentEntity, new BusinessContent(JsonSerializer.SerializeToElement(new { })), schema.VersionId, SubmissionStrategy.OnCompletion);
        });
        await Assert.That(sdk.Execute(local => local.Find(content)!.State)).IsEqualTo(SubmissionState.Draft);
        var observation = sdk.Execute(local =>
        {
            var version = local.Draft(observationEntity, new Observation(content, content, null, null, null, null), strategy: SubmissionStrategy.OnCompletion);
            local.Complete(observationEntity);
            return version;
        });
        await Assert.That(sdk.Execute(local => local.Find(content)!.FreezeOrder < local.Find(observation)!.FreezeOrder)).IsTrue();
        await sdk.SendAsync();
        await Assert.That(handler.Posts.SelectMany(batch => batch).Select(v => v.VersionId).SequenceEqual(new[] { content, observation })).IsTrue();
    }

    [Test]
    [Property("caseId", "sdk-local-versions")]
    public async Task ImmediateSubmissionSendsTwoLinkedObservationVersions()
    {
        using var directory = new SdkDirectory();
        using var handler = new ScriptedBackend();
        using var http = new HttpClient(handler) { BaseAddress = new("http://localhost/") };
        using var sdk = new ObserverClient(directory.Path, EntityId.New(), http);
        var schema = EntityApiTests.Schema();
        var content = EntityApiTests.Business(schema);
        var entity = EntityId.New();
        var observation = new Observation(content.VersionId, content.VersionId, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, null);
        sdk.Execute(local =>
        {
            local.Remember(new(schema, DateTimeOffset.UtcNow));
            local.Remember(new(content, DateTimeOffset.UtcNow));
            local.Draft(entity, observation);
        });
        await sdk.SendAsync();
        sdk.Execute(local => local.Draft(entity, observation with { EndAt = DateTimeOffset.UnixEpoch.AddSeconds(1) }));
        await sdk.SendAsync();
        await Assert.That(handler.Posts.Count).IsEqualTo(2);
        var first = handler.Posts[0].Single();
        var second = handler.Posts[1].Single();
        await Assert.That(second.VersionId).IsNotEqualTo(first.VersionId);
        await Assert.That(second.PreviousVersionId).IsEqualTo(first.VersionId);
        await Assert.That(((Observation)second.Content).EndAt).IsEqualTo(DateTimeOffset.UnixEpoch.AddSeconds(1));
    }

    [Test]
    [Property("caseId", "sdk-confirmation")]
    public async Task SuccessfulResponseConfirmsWholeBatch()
    {
        using var directory = new SdkDirectory();
        using var handler = new ScriptedBackend();
        using var http = new HttpClient(handler) { BaseAddress = new("http://localhost/") };
        using var sdk = new ObserverClient(directory.Path, EntityId.New(), http);
        QueueDependentVersions(sdk);
        await sdk.SendAsync();
        await Assert.That(handler.Posts.Single().Length).IsEqualTo(3);
        await Assert.That(sdk.Status.Saved).IsEqualTo(3);
        await Assert.That(sdk.Status.Pending).IsEqualTo(0);
    }

    [Test]
    [Property("caseId", "sdk-confirmation")]
    public async Task IndexedConflictConfirmsPrefixRejectsTargetAndLeavesSuffixPending()
    {
        using var directory = new SdkDirectory();
        using var handler = new ScriptedBackend { RejectIndex = 1 };
        using var http = new HttpClient(handler) { BaseAddress = new("http://localhost/") };
        using var sdk = new ObserverClient(directory.Path, EntityId.New(), http);
        var versions = QueueDependentVersions(sdk);
        await sdk.SendAsync();
        await Assert.That(sdk.Execute(local => local.Find(versions.Schema)!.State)).IsEqualTo(SubmissionState.Saved);
        await Assert.That(sdk.Execute(local => local.Find(versions.Business)!.State)).IsEqualTo(SubmissionState.Rejected);
        await Assert.That(sdk.Execute(local => local.Find(versions.Inputs)!.State)).IsEqualTo(SubmissionState.Frozen);
        await Assert.That(sdk.Status.Pending).IsEqualTo(2);
    }

    [Test]
    [Property("caseId", "sdk-confirmation")]
    public async Task ContractErrorWithoutIndexStopsSendingAndReportsError()
    {
        using var directory = new SdkDirectory();
        using var handler = new ScriptedBackend { ContractError = true };
        using var http = new HttpClient(handler) { BaseAddress = new("http://localhost/") };
        using var sdk = new ObserverClient(directory.Path, EntityId.New(), http);
        QueueDependentVersions(sdk);
        await sdk.SendAsync();
        await Assert.That(sdk.Status.Error!.Contains("HTTP 400", StringComparison.Ordinal)).IsTrue();
        await sdk.SendAsync();
        await Assert.That(handler.Posts.Count).IsEqualTo(1);
    }

    [Test]
    [Property("caseId", "sdk-rejections")]
    public async Task DiscardingRejectedVersionDiscardsDependentsAndResumesOthers()
    {
        using var directory = new SdkDirectory();
        using var handler = new ScriptedBackend { RejectIndex = 1 };
        using var http = new HttpClient(handler) { BaseAddress = new("http://localhost/") };
        using var sdk = new ObserverClient(directory.Path, EntityId.New(), http);
        var versions = QueueDependentVersions(sdk);
        var independent = sdk.Execute(local => local.Draft(EntityId.New(), EntityApiTests.Schema("other").Content));
        await sdk.SendAsync();
        sdk.Execute(local => local.DiscardRejected());
        await Assert.That(sdk.Execute(local => local.Find(versions.Business))).IsNull();
        await Assert.That(sdk.Execute(local => local.Find(versions.Inputs))).IsNull();
        handler.RejectIndex = null;
        await sdk.SendAsync();
        await Assert.That(handler.Posts[^1].Single().VersionId).IsEqualTo(independent);
        await Assert.That(sdk.Status.Pending).IsEqualTo(0);
    }

    [Test]
    [Property("caseId", "sdk-rejections")]
    public async Task RewritingRejectedVersionUsesReadCurrentVersionAndResendsDependents()
    {
        using var directory = new SdkDirectory();
        using var handler = new ScriptedBackend { RejectIndex = 1 };
        using var http = new HttpClient(handler) { BaseAddress = new("http://localhost/") };
        using var sdk = new ObserverClient(directory.Path, EntityId.New(), http);
        var versions = QueueDependentVersions(sdk);
        await sdk.SendAsync();
        var rejected = sdk.Status.Rejection!.Version.Version;
        handler.Current = new(rejected with { VersionId = VersionId.New() }, DateTimeOffset.UtcNow);
        var current = (await sdk.ReadCurrentAsync(rejected.EntityId))!;
        var replacement = sdk.Execute(local =>
        {
            local.Remember(current);
            return local.ReplaceRejected(rejected.EntityId, rejected.Content, rejected.SchemaVersionId, current.VersionId);
        });
        handler.RejectIndex = null;
        await sdk.SendAsync();
        var sent = handler.Posts[^1];
        await Assert.That(handler.Gets.Single()).IsEqualTo("/entities/" + rejected.EntityId.Value);
        await Assert.That(sent.Select(v => v.VersionId).SequenceEqual(new[] { replacement, versions.Inputs })).IsTrue();
        await Assert.That(sent[0].PreviousVersionId).IsEqualTo(current.VersionId);
        await Assert.That(((InputSet)sent[1].Content).Members.Single()).IsEqualTo(replacement);
        await Assert.That(sdk.Status.Pending).IsEqualTo(0);
    }

    [Test]
    [Property("caseId", "sdk-registration")]
    public async Task OfflineRegistrationPrecedesBusinessVersionsWhenConnectionReturns()
    {
        await using var database = await DatabaseFixture.CreateAsync();
        await using var factory = new HeartbeatApiFactory(database.ConnectionString);
        using var recording = new RecordingBackend(factory.Server.CreateHandler());
        using var onlineHttp = new HttpClient(recording) { BaseAddress = new("http://localhost/") };
        using var directory = new SdkDirectory();
        using var offlineHandler = new ScriptedBackend { Offline = true };
        using var offline = new HttpClient(offlineHandler) { BaseAddress = new("http://localhost/") };
        var owner = EntityId.New();
        var schema = EntityApiTests.Schema();
        var business = EntityId.New();
        VersionId schemaVersion, instanceVersion, businessVersion;
        using (var sdk = new ObserverClient(directory.Path, owner, offline))
        {
            schemaVersion = await sdk.RegisterSchemaAsync(schema.EntityId, (EntitySchema)schema.Content);
            instanceVersion = sdk.RegisterInstance(owner, schemaVersion, new(JsonSerializer.SerializeToElement(new { })));
            businessVersion = sdk.Execute(local => local.Draft(business, new BusinessContent(JsonSerializer.SerializeToElement(new { })), schemaVersion));
            await sdk.SendAsync();
        }
        using var online = new ObserverClient(directory.Path, owner, onlineHttp);
        await online.SendAsync();
        await Assert.That(recording.Posts.SelectMany(batch => batch).Select(v => v.VersionId)
            .SequenceEqual(new[] { schemaVersion, instanceVersion, businessVersion })).IsTrue();
        await Assert.That(online.Status.Saved).IsEqualTo(3);
        await Assert.That(online.Status.Pending).IsEqualTo(0);
        foreach (var id in new[] { schemaVersion, instanceVersion, businessVersion })
        {
            using var read = await onlineHttp.GetAsync($"/versions/{id.Value}");
            await Assert.That(read.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }
    }

    [Test]
    [Property("caseId", "sdk-registration")]
    public async Task NewSchemaContentExtendsCurrentChainAndBusinessReferencesIt()
    {
        await using var database = await DatabaseFixture.CreateAsync();
        await using var factory = new HeartbeatApiFactory(database.ConnectionString);
        using var http = factory.CreateClient();
        var first = EntityApiTests.Schema();
        var second = first with { VersionId = VersionId.New(), PreviousVersionId = first.VersionId,
            Content = new EntitySchema("Items", "Second", "items", JsonSerializer.SerializeToElement(new { })) };
        using var saved = await EntityApiTests.Submit(http, first, second);
        await Assert.That(saved.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var directory = new SdkDirectory();
        using var sdk = new ObserverClient(directory.Path, EntityId.New(), http);
        var content = new EntitySchema("Items", "Third", "items", JsonSerializer.SerializeToElement(new { }));
        var third = await sdk.RegisterSchemaAsync(first.EntityId, content);
        var business = EntityId.New();
        sdk.Execute(local => local.Draft(business, new BusinessContent(JsonSerializer.SerializeToElement(new { })), third));
        await sdk.SendAsync();
        using var chain = await http.GetAsync($"/entities/{first.EntityId.Value}/versions");
        var versions = (await EntityApiTests.Json(chain)).GetProperty("versions").EnumerateArray().Select(VersionJson.ReadSaved).ToArray();
        await Assert.That(versions.Select(v => v.VersionId).SequenceEqual(new[] { first.VersionId, second.VersionId, third })).IsTrue();
        await Assert.That(versions[2].PreviousVersionId).IsEqualTo(second.VersionId);
        await Assert.That(((EntitySchema)versions[2].Content).Description).IsEqualTo("Third");
        using var read = await http.GetAsync($"/entities/{business.Value}");
        await Assert.That((await EntityApiTests.Json(read)).GetProperty("schemaVersionId").GetGuid()).IsEqualTo(third.Value);
    }

    [Test]
    [Property("caseId", "sdk-registration")]
    public async Task ChangedInstanceInformationAfterRestartCreatesReferencedSuccessor()
    {
        await using var database = await DatabaseFixture.CreateAsync();
        await using var factory = new HeartbeatApiFactory(database.ConnectionString);
        using var http = factory.CreateClient();
        using var directory = new SdkDirectory();
        var owner = EntityId.New();
        var schema = EntityApiTests.Schema(fields: """{"programVersion":{"type":"string","required":true,"nullable":false}}""");
        VersionId schemaVersion, first;
        using (var sdk = new ObserverClient(directory.Path, owner, http))
        {
            schemaVersion = await sdk.RegisterSchemaAsync(schema.EntityId, (EntitySchema)schema.Content);
            first = sdk.RegisterInstance(owner, schemaVersion, new(JsonSerializer.SerializeToElement(new { programVersion = "1" })));
            await sdk.SendAsync();
        }
        using var restarted = new ObserverClient(directory.Path, owner, http);
        var second = restarted.RegisterInstance(owner, schemaVersion, new(JsonSerializer.SerializeToElement(new { programVersion = "2" })));
        var observationId = EntityId.New();
        restarted.Execute(local => local.Draft(observationId, new Observation(local.Reference(owner), local.Reference(owner), null, null, null, null)));
        await restarted.SendAsync();
        using var chain = await http.GetAsync($"/entities/{owner.Value}/versions");
        var versions = (await EntityApiTests.Json(chain)).GetProperty("versions").EnumerateArray().Select(VersionJson.ReadSaved).ToArray();
        await Assert.That(versions.Select(v => v.VersionId).SequenceEqual(new[] { first, second })).IsTrue();
        await Assert.That(versions[1].PreviousVersionId).IsEqualTo(first);
        await Assert.That(((BusinessContent)versions[1].Content).Fields.GetProperty("programVersion").GetString()).IsEqualTo("2");
        using var read = await http.GetAsync($"/entities/{observationId.Value}");
        await Assert.That((await EntityApiTests.Json(read)).GetProperty("content").GetProperty("observerVersionId").GetGuid()).IsEqualTo(second.Value);
    }

    [Test]
    [Property("caseId", "foreground-rejections")]
    public async Task ForegroundInstancesSharingRegistryAdoptSavedApplicationFirstVersion()
    {
        await using var database = await DatabaseFixture.CreateAsync();
        await using var factory = new HeartbeatApiFactory(database.ConnectionString);
        using var http = factory.CreateClient();
        using var firstDirectory = new SdkDirectory();
        using var secondDirectory = new SdkDirectory();
        var firstOwner = EntityId.New(); var secondOwner = EntityId.New();
        using var first = new ObserverClient(firstDirectory.Path, firstOwner, http);
        using var second = new ObserverClient(secondDirectory.Path, secondOwner, http);
        var firstRegistration = await ForegroundRegistration.RegisterAsync(first, firstOwner);
        await first.SendAsync();
        var secondRegistration = await ForegroundRegistration.RegisterAsync(second, secondOwner);
        await second.SendAsync();
        var firstRegistry = new LocalApplicationRegistry(firstDirectory.Path + ".apps");
        var firstApplication = firstRegistry.Resolve(ForegroundTimelineTests.Reading(0));
        var secondRegistry = new LocalApplicationRegistry(firstDirectory.Path + ".apps");
        var secondApplication = secondRegistry.Resolve(ForegroundTimelineTests.Reading(0));
        await Assert.That(secondApplication.Id).IsEqualTo(firstApplication.Id);
        var firstStore = new ForegroundStore(first, firstOwner, firstRegistration);
        var secondStore = new ForegroundStore(second, secondOwner, secondRegistration);
        var firstCapture = ForegroundCapture.Create(firstOwner, firstApplication, ForegroundTimelineTests.Reading(0));
        var secondCapture = ForegroundCapture.Create(secondOwner, secondApplication, ForegroundTimelineTests.Reading(0));
        firstStore.Checkpoint(firstCapture, null); firstStore.FinishActive();
        secondStore.Checkpoint(secondCapture, null); secondStore.FinishActive();
        var savedApplication = first.Execute(local => local.Latest(firstApplication.Id)!.Version.VersionId);
        var competing = second.Execute(local => local.Latest(secondApplication.Id)!.Version.VersionId);
        await Assert.That(competing).IsNotEqualTo(savedApplication);
        await new ForegroundDelivery(first, firstRegistry, firstRegistration, TextWriter.Null).SendAsync(CancellationToken.None);
        await new ForegroundDelivery(second, secondRegistry, secondRegistration, TextWriter.Null).SendAsync(CancellationToken.None);
        await Assert.That(second.Status.Rejection).IsNull();
        await Assert.That(second.Status.Pending).IsEqualTo(0);
        using var chain = await http.GetAsync($"/entities/{firstApplication.Id.Value}/versions");
        await Assert.That((await EntityApiTests.Json(chain)).GetProperty("versions").EnumerateArray().Single().GetProperty("versionId").GetGuid()).IsEqualTo(savedApplication.Value);
        using var period = await http.GetAsync($"/entities/{secondCapture.Content.Id.Value}");
        await Assert.That((await EntityApiTests.Json(period)).GetProperty("content").GetProperty("localApplicationVersionId").GetGuid()).IsEqualTo(savedApplication.Value);
        using var observation = await http.GetAsync($"/entities/{secondCapture.ObservationId.Value}");
        await Assert.That(observation.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    [Property("caseId", "foreground-rejections")]
    public async Task OtherForegroundRuleRejectionReportsErrorAndPausesSending()
    {
        await using var database = await DatabaseFixture.CreateAsync();
        await using var factory = new HeartbeatApiFactory(database.ConnectionString);
        using var recording = new RecordingBackend(factory.Server.CreateHandler());
        using var http = new HttpClient(recording) { BaseAddress = new("http://localhost/") };
        using var directory = new SdkDirectory();
        var owner = EntityId.New();
        using var sdk = new ObserverClient(directory.Path, owner, http);
        var registration = await ForegroundRegistration.RegisterAsync(sdk, owner);
        await sdk.SendAsync();
        var registry = new LocalApplicationRegistry(directory.Path + ".apps");
        var capture = ForegroundCapture.Create(owner, registry.Resolve(ForegroundTimelineTests.Reading(0)), ForegroundTimelineTests.Reading(0));
        var store = new ForegroundStore(sdk, owner, registration);
        store.Checkpoint(capture, null);
        // OBSERVATION-6: an invalid interval exercises an unhandled business rejection.
        sdk.Execute(local =>
        {
            var observation = (Observation)local.Latest(capture.ObservationId)!.Version.Content;
            local.Draft(capture.ObservationId, observation with { EndAt = observation.StartAt!.Value.AddSeconds(-1) }, strategy: SubmissionStrategy.OnCompletion);
            local.Complete(capture.ObservationId);
        });
        using var progress = new StringWriter();
        var delivery = new ForegroundDelivery(sdk, registry, registration, progress);
        await delivery.SendAsync(CancellationToken.None);
        await Assert.That(sdk.Status.Rejection!.Version.Rule).IsEqualTo("OBSERVATION-6");
        await Assert.That(progress.ToString().Contains("\"rule\":\"OBSERVATION-6\"", StringComparison.Ordinal)).IsTrue();
        var requests = recording.Posts.Count;
        await delivery.SendAsync(CancellationToken.None);
        await Assert.That(recording.Posts.Count).IsEqualTo(requests);
    }

    private static (VersionId Schema, VersionId Business, VersionId Inputs) QueueDependentVersions(ObserverClient sdk) => sdk.Execute(local =>
    {
        var schema = local.Draft(EntityId.New(), EntityApiTests.Schema().Content);
        var business = local.Draft(EntityId.New(), new BusinessContent(JsonSerializer.SerializeToElement(new { })), schema);
        var inputs = local.Draft(EntityId.New(), new InputSet([business]));
        return (schema, business, inputs);
    });

    private sealed class RecordingBackend(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        internal List<VersionSubmission[]> Posts { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post)
            {
                using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                Posts.Add(json.RootElement.GetProperty("versions").EnumerateArray().Select(VersionJson.ReadSubmission).ToArray());
            }
            return await base.SendAsync(request, cancellationToken);
        }
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
        internal bool ContractError { get; init; }
        internal EntityVersion? Current { get; set; }
        internal List<string> Gets { get; } = [];
        internal bool LoseFirstResponse { get; init; }
        internal int? RejectIndex { get; set; }
        internal int MaximumBatch { get; set; } = int.MaxValue;
        internal List<VersionSubmission[]> Posts { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Offline) throw new HttpRequestException("offline");
            if (request.Method == HttpMethod.Get)
            {
                Gets.Add(request.RequestUri!.AbsolutePath);
                return Current is { } current && request.RequestUri.AbsolutePath == "/entities/" + current.EntityId.Value
                    ? new(HttpStatusCode.OK) { Content = JsonContent.Create(VersionJson.Saved(current)) }
                    : new(HttpStatusCode.NotFound);
            }
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var versions = json.RootElement.GetProperty("versions").EnumerateArray().Select(VersionJson.ReadSubmission).ToArray();
            Posts.Add(versions);
            if (ContractError) return new(HttpStatusCode.BadRequest) { Content = JsonContent.Create(new { detail = "invalid request" }) };
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
