using System.Net;
using Orisumi.Core;
using Orisumi.Observer.Runtime;
using Orisumi.Observer.Sdk;
using Orisumi.Observers.ForegroundState;

namespace Orisumi.Integration.Tests;

public sealed class RuntimeReadingTests
{
    [Test]
    [Property("caseId", "native-views")]
    public async Task ReadReturnsSavedObservationsAndLeavesOutDeletedOnes()
    {
        await using var database = await DatabaseFixture.CreateAsync();
        await using var factory = new OrisumiApiFactory(database.ConnectionString);
        using var http = factory.CreateClient();
        using var directory = new RuntimeHostingTests.RuntimeDirectory();
        using var packages = new PackageStore(directory.Data, null, null, http);
        var owner = EntityId.New();
        await using var observer = new ManagedObserver(packages, new("foreground-state", "Foreground State", "0.1.0", owner.Value, false, false, JsonLines.Json(new { })), http);
        var store = new ForegroundStore(observer.Session, owner, await ForegroundRegistration.RegisterAsync(observer.Session, owner), _ => { });
        var capture = ForegroundCapture.Create(new LocalApplication(EntityId.New(), []), ForegroundTimelineTests.Reading(0));
        store.Checkpoint(capture.Through(capture.EndAt.AddSeconds(2)), null);
        var reader = new RuntimeReader(http);
        var query = JsonLines.Json(new { instanceId = owner.Value, from = capture.StartAt.AddMinutes(-1), to = capture.EndAt.AddMinutes(1), resourceName = "foreground-application-periods" });
        await Assert.That((await reader.ReadAsync(observer, query, CancellationToken.None)).GetProperty("observations").GetArrayLength()).IsEqualTo(0);
        store.FinishActive();
        var saved = await reader.ReadAsync(observer, query, CancellationToken.None);
        await Assert.That(saved.GetProperty("observations").GetArrayLength()).IsEqualTo(1);
        await Assert.That(saved.GetProperty("observations")[0].GetProperty("entityId").GetGuid()).IsEqualTo(capture.ObservationId.Value);
        using var deleted = await http.DeleteAsync("entities/" + capture.ObservationId.Value);
        deleted.EnsureSuccessStatusCode();
        await Assert.That((await reader.ReadAsync(observer, query, CancellationToken.None)).GetProperty("observations").GetArrayLength()).IsEqualTo(0);
        await Assert.That(async () => await new RuntimeReader(http, () => false).ReadAsync(observer, query, CancellationToken.None)).Throws<HttpRequestException>();
    }

    [Test]
    [Property("caseId", "native-views")]
    public async Task PaginationKeepsOneSnapshotWhileCollectionContinues()
    {
        await using var database = await DatabaseFixture.CreateAsync();
        await using var factory = new OrisumiApiFactory(database.ConnectionString);
        using var http = factory.CreateClient();
        using var directory = new RuntimeHostingTests.RuntimeDirectory();
        using var packages = new PackageStore(directory.Data, null, null, http);
        var owner = EntityId.New();
        await using var observer = new ManagedObserver(packages, new("foreground-state", "Foreground State", "0.1.0", owner.Value, false, false, JsonLines.Json(new { })), http);
        var store = new ForegroundStore(observer.Session, owner, await ForegroundRegistration.RegisterAsync(observer.Session, owner), _ => { });
        var captures = Enumerable.Range(0, 4).Select(index => ForegroundCapture.Create(new LocalApplication(EntityId.New(), []), ForegroundTimelineTests.Reading(index))).ToArray();
        foreach (var capture in captures.Take(3)) { store.Checkpoint(capture, null); store.FinishActive(); }
        var reader = new RuntimeReader(http);
        var from = captures[0].StartAt.AddMinutes(-1);
        var to = captures[^1].EndAt.AddMinutes(1);
        var first = await reader.ReadAsync(observer, JsonLines.Json(new { from, to, limit = 1 }), CancellationToken.None);
        var ids = new List<Guid> { first.GetProperty("observations")[0].GetProperty("entityId").GetGuid() };
        store.Checkpoint(captures[3], null);
        store.FinishActive();
        var cursor = first.GetProperty("next").GetString();
        while (cursor is not null)
        {
            var page = await reader.ReadAsync(observer, JsonLines.Json(new { from, to, limit = 1, cursor }), CancellationToken.None);
            ids.AddRange(page.GetProperty("observations").EnumerateArray().Select(item => item.GetProperty("entityId").GetGuid()));
            cursor = page.GetProperty("next").GetString();
        }
        await Assert.That(ids.Distinct().Count()).IsEqualTo(3);
        await Assert.That(ids.Contains(captures[3].ObservationId.Value)).IsFalse();
    }
}
