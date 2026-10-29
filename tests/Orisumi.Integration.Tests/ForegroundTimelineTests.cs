using Orisumi.Core;
using Orisumi.Observers.ForegroundState;

namespace Orisumi.Integration.Tests;

public sealed class ForegroundTimelineTests
{
    private static readonly DateTimeOffset Origin = DateTimeOffset.Parse("2026-10-11T01:00:00Z",
        System.Globalization.CultureInfo.InvariantCulture);

    [Test]
    [Property("caseId", "foreground-application-period-boundaries")]
    public async Task ContinuousConfirmationExtendsSamePeriodAndSwitchClosesAtNewConfirmation()
    {
        var timeline = new ForegroundTimeline(TimeSpan.FromSeconds(3));
        var app = new LocalApplication(EntityId.New(), []);
        var first = timeline.Accept(app, Reading(0)).Single();
        var extended = timeline.Accept(app, Reading(1)).Single();
        var switched = timeline.Accept(new LocalApplication(EntityId.New(), []), Reading(2));
        await Assert.That(first.ObservationId).IsEqualTo(extended.ObservationId);
        await Assert.That(first.Content.Id).IsEqualTo(extended.Content.Id);
        await Assert.That(first.EndAt).IsEqualTo(Origin);
        await Assert.That(extended.EndAt).IsEqualTo(Origin.AddSeconds(1));
        await Assert.That(switched.Count).IsEqualTo(2);
        await Assert.That(switched[0].ObservationId).IsEqualTo(first.ObservationId);
        await Assert.That(switched[0].EndAt).IsEqualTo(switched[1].StartAt);
        await Assert.That(switched[1].Content.Id == first.Content.Id).IsFalse();
    }

    [Test]
    [Property("caseId", "foreground-application-period-boundaries")]
    public async Task RenameAndTimeZoneChangeSplitContentWithoutReplacingApplicationIdentity()
    {
        var timeline = new ForegroundTimeline(TimeSpan.FromSeconds(3));
        var app = new LocalApplication(EntityId.New(), []);
        var first = timeline.Accept(app, Reading(0)).Single();
        var renamed = timeline.Accept(app, Reading(1) with { Name = null });
        var zoneChanged = timeline.Accept(app, Reading(2) with { Name = null, TimeZone = "Etc/UTC" });
        await Assert.That(renamed.Count).IsEqualTo(2);
        await Assert.That(renamed[1].Application.Id).IsEqualTo(app.Id);
        await Assert.That(renamed[1].Content.Name).IsNull();
        await Assert.That(first.Content.Name).IsEqualTo("App");
        await Assert.That(zoneChanged.Count).IsEqualTo(2);
        await Assert.That(zoneChanged[0].TimeZone).IsEqualTo("Asia/Shanghai");
        await Assert.That(zoneChanged[1].TimeZone).IsEqualTo("Etc/UTC");
    }

    [Test]
    [Arguments("read-failure")]
    [Arguments("long-gap")]
    [Arguments("sleep")]
    [Arguments("clock-backward")]
    [Arguments("clock-forward")]
    [Property("caseId", "foreground-application-period-boundaries")]
    public async Task InterruptionsStartNewPeriodWithoutFillingTheGap(string interruption)
    {
        var timeline = new ForegroundTimeline(TimeSpan.FromSeconds(3));
        var app = new LocalApplication(EntityId.New(), []);
        timeline.Accept(app, Reading(0));
        var previous = timeline.Accept(app, Reading(1)).Single();
        var next = interruption switch
        {
            "long-gap" => Reading(10),
            "sleep" => Reading(2) with { Awake = TimeSpan.FromSeconds(1.1) },
            "clock-backward" => Reading(2) with { ObservedAt = Origin.AddSeconds(-1) },
            "clock-forward" => Reading(2) with { ObservedAt = Origin.AddSeconds(2.5) },
            _ => Reading(2),
        };
        if (interruption == "read-failure") timeline.Interrupt();
        var resumed = timeline.Accept(app, next);
        await Assert.That(resumed.Count).IsEqualTo(interruption == "read-failure" ? 1 : 2);
        if (resumed.Count == 2) await Assert.That(resumed[0].EndAt).IsEqualTo(previous.EndAt);
        await Assert.That(previous.EndAt).IsEqualTo(Origin.AddSeconds(1));
        await Assert.That(resumed[^1].ObservationId == previous.ObservationId).IsFalse();
        await Assert.That(resumed[^1].StartAt).IsEqualTo(next.ObservedAt);
    }

    internal static ForegroundApplicationReading Reading(int second) =>
        new("app", "App", "/app", Origin.AddSeconds(second), "Asia/Shanghai",
            Elapsed: TimeSpan.FromSeconds(second), Awake: TimeSpan.FromSeconds(second));
}
