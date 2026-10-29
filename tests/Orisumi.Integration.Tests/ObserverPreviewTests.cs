using System.Net;
using System.Text.Json;
using Orisumi.Core;
using Orisumi.Observer.Runtime.Store;
using Orisumi.Observers.ForegroundState;

namespace Orisumi.Integration.Tests;

public sealed class ObserverPreviewTests
{
    [Test]
    [Property("caseId", "foreground-submission")]
    public async Task LivePreviewRecoversAcrossServerAndStoreRestartsAndCompletesOnce()
    {
        await using var database = await DatabaseFixture.CreateAsync();
        await using var firstFactory = new OrisumiApiFactory(database.ConnectionString);
        using var firstHandler = new RecordingHandler(firstFactory.Server.CreateHandler());
        using var firstHttp = new HttpClient(firstHandler) { BaseAddress = new("http://localhost/") };
        var directory = Directory.CreateTempSubdirectory("orisumi-live-preview-");
        try
        {
            var path = Path.Combine(directory.FullName, "state.sqlite");
            var owner = EntityId.New();
            ForegroundCapture capture;
            ForegroundRegistration registration;
            long lastSequence;
            using (var runtimeStore = new ObserverStore(path, owner, firstHttp))
            {
                registration = await ForegroundRegistration.RegisterAsync(new LocalObserverSession(runtimeStore), owner);
                var registry = new LocalApplicationRegistry(Path.Combine(directory.FullName, "apps"));
                var reading = ForegroundTimelineTests.Reading(0);
                capture = ForegroundCapture.Create(registry.Resolve(reading), reading);
                var store = new ForegroundStore(new LocalObserverSession(runtimeStore), owner, registration);
                store.Checkpoint(capture, null);
                await runtimeStore.SendAsync();
                await Assert.That(runtimeStore.Status.Error).IsNull();
                await ReadEnd(firstHttp, capture, capture.EndAt);
                firstHandler.FailNextPreview = true;
                store.Checkpoint(capture.Through(reading.ObservedAt.AddSeconds(1)), null);
                await runtimeStore.SendAsync();
                store.Checkpoint(capture.Through(reading.ObservedAt.AddSeconds(2)), null);
                await runtimeStore.SendAsync();
                await ReadEnd(firstHttp, capture, reading.ObservedAt.AddSeconds(2));
                lastSequence = firstHandler.Previews[^1].GetProperty("sequence").GetInt64();
                using var noVersions = await firstHttp.GetAsync($"/entities/{capture.ObservationId.Value}/versions");
                await Assert.That(noVersions.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
            }
            await using var secondFactory = new OrisumiApiFactory(database.ConnectionString);
            using var secondHandler = new RecordingHandler(secondFactory.Server.CreateHandler());
            using var http = new HttpClient(secondHandler) { BaseAddress = new("http://localhost/") };
            using var absent = await http.GetAsync($"/observation-previews/{capture.ObservationId.Value}");
            await Assert.That(absent.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
            using var recovered = new ObserverStore(path, owner, http);
            await recovered.SendAsync();
            await Assert.That(secondHandler.Previews.Single().GetProperty("sequence").GetInt64()).IsGreaterThan(lastSequence);
            await ReadEnd(http, capture, capture.EndAt.AddSeconds(2));
            var resumedStore = new ForegroundStore(new LocalObserverSession(recovered), owner, registration);
            resumedStore.FinishActive();
            await recovered.SendAsync();
            await recovered.SendAsync();
            await Assert.That(secondHandler.Previews.Count).IsEqualTo(1);
            using var removed = await http.GetAsync($"/observation-previews/{capture.ObservationId.Value}");
            await Assert.That(removed.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
            using var history = await http.GetAsync($"/entities/{capture.ObservationId.Value}/versions");
            await Assert.That((await EntityApiTests.Json(history)).GetProperty("versions").GetArrayLength()).IsEqualTo(1);
        }
        finally { directory.Delete(true); }
    }

    [Test]
    [Property("caseId", "runtime-local-versions")]
    public async Task VersionConflictStopsPublishingEvenAfterLocalUpdatesAndRestart()
    {
        await using var database = await DatabaseFixture.CreateAsync();
        await using var factory = new OrisumiApiFactory(database.ConnectionString);
        using var handler = new RecordingHandler(factory.Server.CreateHandler());
        using var http = new HttpClient(handler) { BaseAddress = new("http://localhost/") };
        var directory = Directory.CreateTempSubdirectory("orisumi-preview-conflict-");
        try
        {
            var owner = EntityId.New();
            var path = Path.Combine(directory.FullName, "state.sqlite");
            var schema = EntityApiTests.Schema();
            var content = EntityApiTests.Business(schema);
            using var saved = await EntityApiTests.Submit(http, schema, content);
            saved.EnsureSuccessStatusCode();
            var id = EntityId.New();
            var observation = new Observation(content.VersionId, content.VersionId, null, null, null, null);
            using (var runtimeStore = new ObserverStore(path, owner, http))
            {
                runtimeStore.Execute(local =>
                {
                    local.Remember(new(content, DateTimeOffset.UtcNow));
                    local.Draft(id, observation, strategy: SubmissionStrategy.OnCompletion, publishPreview: true);
                });
                await runtimeStore.SendAsync();
                using var advanced = await EntityApiTests.Submit(http, new VersionSubmission(id, VersionId.New(), null, null, observation));
                advanced.EnsureSuccessStatusCode();
                await runtimeStore.SendAsync();
            }
            using var restarted = new ObserverStore(path, owner, http);
            restarted.Execute(local => local.Draft(id, observation with { TimeZone = "Asia/Shanghai" },
                strategy: SubmissionStrategy.OnCompletion, publishPreview: true));
            var sent = handler.Previews.Count;
            await restarted.SendAsync();
            await Assert.That(handler.Previews.Count).IsEqualTo(sent);
            await Assert.That(restarted.Status.Error).IsNull();
        }
        finally { directory.Delete(true); }
    }

    [Test]
    [Property("caseId", "foreground-submission")]
    public async Task ContentReferencesDoNotFreezeTheParentObservationAndSeparateRepeatedPeriods()
    {
        await using var database = await DatabaseFixture.CreateAsync();
        await using var factory = new OrisumiApiFactory(database.ConnectionString);
        using var http = factory.CreateClient();
        var directory = Directory.CreateTempSubdirectory("orisumi-period-reference-");
        try
        {
            var owner = EntityId.New();
            using var runtimeStore = new ObserverStore(Path.Combine(directory.FullName, "state.sqlite"), owner, http);
            var registration = await ForegroundRegistration.RegisterAsync(new LocalObserverSession(runtimeStore), owner);
            var registry = new LocalApplicationRegistry(Path.Combine(directory.FullName, "apps"));
            var reading = ForegroundTimelineTests.Reading(0);
            var application = registry.Resolve(reading);
            var first = ForegroundCapture.Create(application, reading);
            var store = new ForegroundStore(new LocalObserverSession(runtimeStore), owner, registration);
            store.Checkpoint(first, null);
            var titleSchema = EntityApiTests.Schema("test-title-contents", """
                {"foregroundApplicationPeriodVersionId":{"type":"reference","description":"The parent activity content version","required":true,"nullable":false},
                 "title":{"type":"string","description":"Window title as read","required":true,"nullable":false}}
                """);
            using var setup = await EntityApiTests.Submit(http, titleSchema);
            setup.EnsureSuccessStatusCode();
            runtimeStore.Execute(local => local.Remember(new(titleSchema, DateTimeOffset.UtcNow)));
            for (var index = 0; index < 3; index++)
            {
                runtimeStore.Execute(local => local.Draft(EntityId.New(), new BusinessContent(JsonSerializer.SerializeToElement(new
                {
                    foregroundApplicationPeriodVersionId = local.Reference(first.Content.Id).Value, title = $"Title {index}",
                })), titleSchema.VersionId));
                store.Checkpoint(first.Through(reading.ObservedAt.AddSeconds(index)), null);
                await runtimeStore.SendAsync();
            }
            using var noHistory = await http.GetAsync($"/entities/{first.ObservationId.Value}");
            await Assert.That(noHistory.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
            var secondReading = ForegroundTimelineTests.Reading(3);
            var second = ForegroundCapture.Create(application, secondReading);
            await Assert.That(second.Content.Id).IsNotEqualTo(first.Content.Id);
            store.Checkpoint(second, first.Through(secondReading.ObservedAt));
            await runtimeStore.SendAsync();
            using var completed = await http.GetAsync($"/entities/{first.ObservationId.Value}/versions");
            await Assert.That((await EntityApiTests.Json(completed)).GetProperty("versions").GetArrayLength()).IsEqualTo(1);
            using var removed = await http.GetAsync($"/observation-previews/{first.ObservationId.Value}");
            await Assert.That(removed.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
            await ReadEnd(http, second, secondReading.ObservedAt);
        }
        finally { directory.Delete(true); }
    }

    private static async Task ReadEnd(HttpClient http, ForegroundCapture capture, DateTimeOffset expected)
    {
        using var response = await http.GetAsync($"/observation-previews/{capture.ObservationId.Value}");
        response.EnsureSuccessStatusCode();
        await Assert.That((await EntityApiTests.Json(response)).GetProperty("content").GetProperty("endAt").GetDateTimeOffset()).IsEqualTo(expected);
    }

    private sealed class RecordingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        internal List<JsonElement> Previews { get; } = [];
        internal bool FailNextPreview { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Put)
            {
                Previews.Add(JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(cancellationToken)));
                if (FailNextPreview) { FailNextPreview = false; return new(HttpStatusCode.ServiceUnavailable); }
            }
            return await base.SendAsync(request, cancellationToken);
        }
    }
}
