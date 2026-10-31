using System.Text.Json;
using Orisumi.Core;
using Orisumi.Observer.Sdk;
using Orisumi.Observers.ForegroundState;

namespace Orisumi.Integration.Tests;

public sealed class ForegroundIconTests
{
    [Test, Property("caseId", "foreground-icons")]
    public async Task IconIsCapturedAtPeriodStartAndSharedWithoutChangingApplicationIdentity()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var factory = new OrisumiApiFactory(db.ConnectionString);
        using var http = factory.CreateClient();
        var fixture = await ForegroundTestStore.CreateAsync(http);
        var timeline = new ForegroundTimeline(TimeSpan.FromSeconds(3));
        var firstIcon = new BinaryContent([1, 2], "image/png"); var secondIcon = new BinaryContent([3, 4], "image/png");
        var reads = 0; var icon = firstIcon;
        ForegroundApplicationReading Reading(int second) => ForegroundTimelineTests.Reading(second) with { ReadIcon = () => { reads++; return icon; } };
        var application = fixture.Applications.Resolve(Reading(0));
        var first = timeline.Accept(application, Reading(0)).Single(); fixture.Store.Checkpoint(first, null);
        icon = secondIcon;
        var ongoing = timeline.Accept(application, Reading(1)).Single(); fixture.Store.Checkpoint(ongoing, null);
        await Assert.That(reads).IsEqualTo(1);
        await Assert.That(ongoing.Content.Icon!.Digest).IsEqualTo(firstIcon.Digest);
        fixture.Store.FinishActive();
        for (var second = 2; second <= 3; second++)
        {
            timeline.Interrupt(); fixture.Store.Checkpoint(timeline.Accept(application, Reading(second)).Single(), null); fixture.Store.FinishActive();
        }
        await Assert.That(fixture.Diagnostics.Count).IsEqualTo(0);
        await Assert.That(reads).IsEqualTo(3);
        var periods = await fixture.Periods();
        await Assert.That(periods.Select(v => ((Observation)v.Content).Fields.GetProperty("icon").GetProperty("digest").GetString()).SequenceEqual([firstIcon.Digest, secondIcon.Digest, secondIcon.Digest])).IsTrue();
        foreach (var (file, bytes) in new[] { (firstIcon, new byte[] { 1, 2 }), (secondIcon, new byte[] { 3, 4 }) })
            await Assert.That((await http.GetByteArrayAsync("contents/" + file.Digest)).SequenceEqual(bytes)).IsTrue();
        await Assert.That(await BinaryContentTests.Count(db.ConnectionString, "contents")).IsEqualTo(2);
        await Assert.That((await fixture.Data.ListAsync("things?resourceName=local-applications")).Count).IsEqualTo(1);
    }

    [Test, Property("caseId", "foreground-icons")]
    public async Task MissingOrFailedIconDoesNotPreventSavingAPeriod()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var factory = new OrisumiApiFactory(db.ConnectionString);
        using var http = factory.CreateClient();
        var fixture = await ForegroundTestStore.CreateAsync(http);
        foreach (var fail in new[] { false, true })
        {
            var reading = ForegroundTimelineTests.Reading(0) with { ReadIcon = () => fail ? throw new IOException("Icon unavailable") : null };
            fixture.Store.Checkpoint(ForegroundCapture.Create(fixture.Applications.Resolve(reading), reading), null); fixture.Store.FinishActive();
        }
        await Assert.That((await fixture.Periods()).All(v => ((Observation)v.Content).Fields.GetProperty("icon").ValueKind == JsonValueKind.Null)).IsTrue();
        await Assert.That((await fixture.Observations()).Length).IsEqualTo(2);
        await Assert.That(await BinaryContentTests.Count(db.ConnectionString, "contents")).IsEqualTo(0);
    }
}
