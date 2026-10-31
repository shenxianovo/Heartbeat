using Orisumi.Core;
using Orisumi.Observer.Runtime.Forwarding;
using Orisumi.Observer.Sdk;
using Orisumi.Observers.ForegroundState;

namespace Orisumi.Integration.Tests;

public sealed class ForegroundSubmissionTests
{
    [Test, Property("caseId", "foreground-submission")]
    public async Task OngoingPeriodIsWrittenOnceWhenItEnds()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var factory = new OrisumiApiFactory(db.ConnectionString);
        using var http = factory.CreateClient();
        var local = await ForegroundTestStore.CreateAsync(http);
        var capture = local.Capture(); local.Store.Checkpoint(capture, null);
        var extended = capture.Through(ForegroundTimelineTests.Reading(1).ObservedAt);
        local.Store.Checkpoint(extended, null);
        await Assert.That((await local.Observations()).Length).IsEqualTo(0);
        local.Store.FinishActive(); local.Store.FinishActive();
        var saved = (await local.Observations()).Single();
        await Assert.That(saved.PreviousVersionId).IsNull();
        var observation = (Observation)saved.Content;
        await Assert.That(observation.StartAt).IsEqualTo(capture.StartAt);
        await Assert.That(observation.EndAt).IsEqualTo(extended.EndAt);
        await Assert.That(local.Diagnostics.Count).IsEqualTo(0);
    }

    [Test, Property("caseId", "foreground-submission")]
    public async Task OfflineCollectionContinuesAndAllPeriodsArriveAfterReconnection()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var factory = new OrisumiApiFactory(db.ConnectionString);
        using var http = factory.CreateClient();
        var fixture = await ForegroundTestStore.CreateAsync(http);
        using var directory = new RuntimeHostingTests.RuntimeDirectory();
        var connected = false;
        await using var queue = new WriteQueue(directory.Data, http, () => connected);
        await using var server = new ForwardingServer(); await server.StartAsync(CancellationToken.None);
        var id = Guid.CreateVersion7(); server.Add(id, queue);
        using var local = new HttpClient { BaseAddress = new(server.InstanceAddress(id)) };
        await using var data = new DataClient(local, http, fixture.Data.Observer);
        await ForegroundRegistration.RegisterAsync(data);
        var store = new ForegroundStore(data, fixture.Device, fixture.Diagnostics.Add);
        var first = fixture.Capture(); var second = fixture.Capture(1);
        store.Checkpoint(first, null); store.Checkpoint(second, first); store.FinishActive();
        await Assert.That(queue.Pending).IsEqualTo(3);
        await Assert.That((await fixture.Periods()).Length).IsEqualTo(0);
        connected = true;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (queue.Pending != 0) await Task.Delay(25, timeout.Token);
        await Assert.That(queue.Rejected).IsEqualTo(0);
        await Assert.That((await fixture.Periods()).Length).IsEqualTo(2);
    }

    [Test, Property("caseId", "foreground-application-period-boundaries")]
    public async Task EventsBetweenPollsArePreservedAndSleepClosesWithoutExtending()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var factory = new OrisumiApiFactory(db.ConnectionString);
        using var http = factory.CreateClient();
        var local = await ForegroundTestStore.CreateAsync(http);
        var collector = new ForegroundCollector(local.Applications, local.Store, TimeSpan.FromSeconds(1),
            () => ForegroundTimelineTests.Reading(2), TextWriter.Null);
        collector.Observe(ForegroundTimelineTests.Reading(0));
        foreach (var ms in new[] { 100, 200 }) collector.Observe(ForegroundTimelineTests.Reading(0) with {
            Name = ms.ToString(System.Globalization.CultureInfo.InvariantCulture), ObservedAt = ForegroundTimelineTests.Reading(0).ObservedAt.AddMilliseconds(ms),
            Elapsed = TimeSpan.FromMilliseconds(ms), Awake = TimeSpan.FromMilliseconds(ms) });
        await Assert.That((await local.Observations()).Length).IsEqualTo(2);
        collector.Sleep(); collector.Poll();
        await Assert.That((await local.Observations()).Length).IsEqualTo(3);
        collector.Wake(); collector.Stop();
        var observations = await local.Observations();
        await Assert.That(observations.Length).IsEqualTo(4);
        await Assert.That(((Observation)observations[2].Content).EndAt).IsEqualTo(ForegroundTimelineTests.Reading(0).ObservedAt.AddMilliseconds(200));
    }
}
