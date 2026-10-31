using Orisumi.Core;
using Orisumi.Observers.ForegroundState;

namespace Orisumi.Integration.Tests;

public sealed class ForegroundSubmissionTests
{
    [Test]
    [Property("caseId", "foreground-submission")]
    public async Task OngoingPeriodIsSubmittedOnceWhenItEnds()
    {
        var observer = EntityId.New();
        using var local = new ForegroundTestStore(observer);
        var capture = Capture();
        local.Store.Checkpoint(capture, null);
        var extended = capture.Through(ForegroundTimelineTests.Reading(1).ObservedAt);
        local.Store.Checkpoint(extended, null);
        await Assert.That(local.Observations().Count).IsEqualTo(0);
        local.Store.FinishActive();
        local.Store.FinishActive();
        var saved = local.Observations().Single();
        await Assert.That(saved.Version.EntityId.Value).IsEqualTo(capture.ObservationId.Value);
        await Assert.That(saved.Version.PreviousVersionId).IsNull();
        var observation = (Observation)saved.Version.Content;
        await Assert.That(observation.StartAt).IsEqualTo(capture.StartAt);
        await Assert.That(observation.EndAt).IsEqualTo(extended.EndAt);
        // The period, its content and the local application were one submission.
        var submission = local.Backend.Posts[^1];
        await Assert.That(submission.Select(version => version.EntityId)
            .SequenceEqual(new[] { capture.Application.Id, capture.Content.Id, capture.ObservationId })).IsTrue();
    }

    [Test]
    [Property("caseId", "foreground-submission")]
    public async Task FailedSubmissionIsDiagnosedAndCollectionContinues()
    {
        var observer = EntityId.New();
        using var local = new ForegroundTestStore(observer);
        var first = Capture();
        var second = Capture();
        local.Store.Checkpoint(first, null);
        local.Backend.Offline = true;
        local.Store.Checkpoint(second, first);
        await Assert.That(local.Diagnostics.Count).IsEqualTo(1);
        local.Backend.Offline = false;
        local.Store.FinishActive();
        await Assert.That(local.Observations().Single().Version.EntityId.Value).IsEqualTo(second.ObservationId.Value);
    }

    [Test]
    [Property("caseId", "foreground-application-period-boundaries")]
    public async Task EventsBetweenPollsArePreservedAndSleepClosesWithoutExtending()
    {
        var observer = EntityId.New();
        using var local = new ForegroundTestStore(observer);
        var collector = new ForegroundCollector(observer, new LocalApplicationRegistry(local.Path + ".apps"),
            local.Store, TimeSpan.FromSeconds(1), () => ForegroundTimelineTests.Reading(2), TextWriter.Null, TextWriter.Null);
        collector.Observe(ForegroundTimelineTests.Reading(0));
        collector.Observe(ForegroundTimelineTests.Reading(0) with { Name = "B", ObservedAt = ForegroundTimelineTests.Reading(0).ObservedAt.AddMilliseconds(100), Elapsed = TimeSpan.FromMilliseconds(100), Awake = TimeSpan.FromMilliseconds(100) });
        collector.Observe(ForegroundTimelineTests.Reading(0) with { Name = "C", ObservedAt = ForegroundTimelineTests.Reading(0).ObservedAt.AddMilliseconds(200), Elapsed = TimeSpan.FromMilliseconds(200), Awake = TimeSpan.FromMilliseconds(200) });
        await Assert.That(local.Observations().Count).IsEqualTo(2);
        collector.Sleep();
        collector.Poll();
        await Assert.That(local.Observations().Count).IsEqualTo(3);
        collector.Wake();
        await Assert.That(local.Observations().Count).IsEqualTo(3);
        collector.Stop();
        await Assert.That(local.Observations().Count).IsEqualTo(4);
        var end = ((Observation)local.Observations()[2].Version.Content).EndAt;
        await Assert.That(end).IsEqualTo(ForegroundTimelineTests.Reading(0).ObservedAt.AddMilliseconds(200));
    }

    private static ForegroundCapture Capture() => ForegroundCapture.Create(new LocalApplication(EntityId.New(), []), ForegroundTimelineTests.Reading(0));

}
