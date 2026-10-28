using System.Net;
using System.Text.Json;
using Orisumi.Core;
using Orisumi.Observer.Runtime;
using Orisumi.Observer.Sdk;
using Orisumi.Observers.ForegroundState;

namespace Orisumi.Integration.Tests;

public sealed class RuntimeReadingTests
{
    [Test]
    [Property("caseId", "native-views")]
    public async Task UploadDoesNotDuplicateAndKnownDeletionDoesNotReappearOffline()
    {
        await using var database = await DatabaseFixture.CreateAsync();
        await using var factory = new OrisumiApiFactory(database.ConnectionString);
        using var gate = new BackendGate(factory.Server.CreateHandler());
        using var http = new HttpClient(gate) { BaseAddress = new("http://localhost/") };
        using var directory = new RuntimeHostingTests.RuntimeDirectory();
        using var packages = new PackageStore(directory.Data, null, null, http);
        var owner = EntityId.New();
        var installed = new InstalledObserver("foreground-state", "Foreground State", "0.1.0", owner.Value, false, false, JsonLines.Json(new { }));
        await using var observer = new ManagedObserver(packages, installed, http);
        var session = new LocalObserverSession(observer.Sdk);
        var registration = await ForegroundRegistration.RegisterAsync(session, owner);
        var store = new ForegroundStore(session, owner, registration);
        var capture = ForegroundCapture.Create(owner, new LocalApplication(EntityId.New(), []), ForegroundTimelineTests.Reading(0));
        store.Checkpoint(capture.Through(capture.EndAt.AddSeconds(2)), null);
        var reader = new RuntimeReader(http);
        var query = JsonLines.Json(new { instanceId = owner.Value, from = capture.StartAt.AddMinutes(-1), to = capture.EndAt.AddMinutes(1), resourceName = "foreground-application-periods" });
        var draft = await reader.ReadAsync(observer, query, CancellationToken.None);
        await Assert.That(draft.GetProperty("observations").GetArrayLength()).IsEqualTo(1);
        await Assert.That(draft.GetProperty("observations")[0].GetProperty("state").GetString()).IsEqualTo("draft");
        store.FinishActive();
        gate.Writes = true;
        await observer.Sdk.SendAsync();
        var saved = await reader.ReadAsync(observer, query, CancellationToken.None);
        await Assert.That(saved.GetProperty("observations").GetArrayLength()).IsEqualTo(1);
        await Assert.That(saved.GetProperty("observations")[0].GetProperty("state").GetString()).IsEqualTo("saved");
        using var deleted = await http.DeleteAsync("entities/" + capture.ObservationId.Value);
        deleted.EnsureSuccessStatusCode();
        var afterDeletion = await reader.ReadAsync(observer, query, CancellationToken.None);
        await Assert.That(afterDeletion.GetProperty("observations").GetArrayLength()).IsEqualTo(0);
        gate.Offline = true;
        var offline = await new RuntimeReader(http).ReadAsync(observer, query, CancellationToken.None);
        await Assert.That(offline.GetProperty("complete").GetBoolean()).IsFalse();
        await Assert.That(offline.GetProperty("observations").GetArrayLength()).IsEqualTo(0);
    }

    [Test]
    [Property("caseId", "native-views")]
    public async Task PaginationKeepsOneSnapshotWhileCollectionContinues()
    {
        using var directory = new RuntimeHostingTests.RuntimeDirectory();
        using var http = new HttpClient(new RuntimeProtocolTests.OfflineHandler()) { BaseAddress = new("http://localhost/") };
        using var packages = new PackageStore(directory.Data, null, null, http);
        var owner = EntityId.New();
        await using var observer = new ManagedObserver(packages, new("foreground-state", "Foreground State", "0.1.0", owner.Value, false, false, JsonLines.Json(new { })), http);
        var session = new LocalObserverSession(observer.Sdk);
        var registration = await ForegroundRegistration.RegisterAsync(session, owner);
        var store = new ForegroundStore(session, owner, registration);
        var captures = Enumerable.Range(0, 4).Select(index => ForegroundCapture.Create(owner,
            new LocalApplication(EntityId.New(), []), ForegroundTimelineTests.Reading(index))).ToArray();
        foreach (var capture in captures.Take(3)) { store.Checkpoint(capture, null); store.FinishActive(); }
        var reader = new RuntimeReader(http);
        var from = captures[0].StartAt.AddMinutes(-1);
        var to = captures[^1].EndAt.AddMinutes(1);
        var first = await reader.ReadAsync(observer, JsonLines.Json(new { from, to, limit = 1 }), CancellationToken.None);
        var ids = new List<Guid> { first.GetProperty("observations")[0].GetProperty("version").GetProperty("entityId").GetGuid() };
        store.Checkpoint(captures[3], null);
        var cursor = first.GetProperty("next").GetString();
        while (cursor is not null)
        {
            var page = await reader.ReadAsync(observer, JsonLines.Json(new { from, to, limit = 1, cursor }), CancellationToken.None);
            ids.AddRange(page.GetProperty("observations").EnumerateArray().Select(item => item.GetProperty("version").GetProperty("entityId").GetGuid()));
            cursor = page.GetProperty("next").GetString();
        }
        await Assert.That(ids.Distinct().Count()).IsEqualTo(3);
        await Assert.That(ids.Contains(captures[3].ObservationId.Value)).IsFalse();
    }

    private sealed class BackendGate(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public bool Writes { get; set; }
        public bool Offline { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Offline || !Writes && request.Method is { } method && (method == HttpMethod.Post || method == HttpMethod.Put)
                ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)) : base.SendAsync(request, cancellationToken);
    }
}
