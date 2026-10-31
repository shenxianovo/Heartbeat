using System.Net;
using Orisumi.Core;
using Orisumi.Observer.Runtime;
using Orisumi.Observer.Sdk;
using Orisumi.Observers.ForegroundState;

namespace Orisumi.Integration.Tests;

public sealed class ForegroundIconTests
{
    [Test, Property("caseId", "foreground-icons")]
    public async Task IconIsCapturedAtPeriodStartAndSharedWithoutChangingApplicationIdentity()
    {
        await using var database = await DatabaseFixture.CreateAsync();
        await using var factory = new OrisumiApiFactory(database.ConnectionString);
        using var http = factory.CreateClient();
        var owner = EntityId.New();
        var runtime = new RuntimeObserverSession(http, owner);
        var diagnostics = new List<string>();
        var registration = await ForegroundRegistration.RegisterAsync(runtime, owner);
        var store = new ForegroundStore(runtime, owner, registration, diagnostics.Add);
        var application = new LocalApplication(EntityId.New(), []);
        var timeline = new ForegroundTimeline(TimeSpan.FromSeconds(3));
        var firstIcon = new BinaryContent([1, 2], "image/png");
        var secondIcon = new BinaryContent([3, 4], "image/png");
        var reads = 0;
        BinaryContent? icon = firstIcon;
        ForegroundApplicationReading Reading(int second) => ForegroundTimelineTests.Reading(second) with { ReadIcon = () => { reads++; return icon; } };
        var first = timeline.Accept(application, Reading(0)).Single();
        store.Checkpoint(first, null);
        icon = secondIcon;
        var ongoing = timeline.Accept(application, Reading(1)).Single();
        store.Checkpoint(ongoing, null);
        await Assert.That(reads).IsEqualTo(1);
        await Assert.That(ongoing.Content.Icon!.Digest).IsEqualTo(firstIcon.Digest);
        store.FinishActive();
        timeline.Interrupt();
        var second = timeline.Accept(application, Reading(2)).Single();
        store.Checkpoint(second, null);
        store.FinishActive();
        timeline.Interrupt();
        var third = timeline.Accept(application, Reading(3)).Single();
        store.Checkpoint(third, null);
        store.FinishActive();
        await Assert.That(diagnostics.Count).IsEqualTo(0);
        await Assert.That(reads).IsEqualTo(3);
        await Assert.That(second.Application.Id).IsEqualTo(first.Application.Id);
        await Assert.That(second.Content.Icon!.Digest).IsEqualTo(secondIcon.Digest);
        await Assert.That(third.Content.Icon!.Digest).IsEqualTo(secondIcon.Digest);
        foreach (var capture in new[] { first, second, third })
        {
            var version = await runtime.ReadCurrentAsync(capture.Content.Id, CancellationToken.None);
            var fields = ((BusinessContent)version!.Content).Fields;
            await Assert.That(fields.GetProperty("icon").GetProperty("digest").GetString()).IsEqualTo(capture.Content.Icon!.Digest);
            await Assert.That(fields.GetProperty("icon").GetProperty("mediaType").GetString()).IsEqualTo("image/png");
            using var response = await http.GetAsync("contents/" + capture.Content.Icon.Digest);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That((await response.Content.ReadAsByteArrayAsync()).SequenceEqual(capture.Content.Icon.Payload.GetProperty("data").GetBytesFromBase64())).IsTrue();
        }
        await Assert.That(await BinaryContentTests.Count(database.ConnectionString, "contents")).IsEqualTo(2);
    }

    [Test, Property("caseId", "foreground-icons")]
    public async Task MissingOrFailedIconDoesNotPreventSavingAPeriod()
    {
        await using var database = await DatabaseFixture.CreateAsync();
        await using var factory = new OrisumiApiFactory(database.ConnectionString);
        using var http = factory.CreateClient();
        var owner = EntityId.New();
        var runtime = new RuntimeObserverSession(http, owner);
        var store = new ForegroundStore(runtime, owner, await ForegroundRegistration.RegisterAsync(runtime, owner), _ => { });
        foreach (var fail in new[] { false, true })
        {
            var capture = ForegroundCapture.Create(new(EntityId.New(), []), ForegroundTimelineTests.Reading(0) with {
                ReadIcon = () => fail ? throw new IOException("Icon unavailable") : null });
            store.Checkpoint(capture, null);
            store.FinishActive();
            var saved = await runtime.ReadCurrentAsync(capture.Content.Id, CancellationToken.None);
            await Assert.That(((BusinessContent)saved!.Content).Fields.GetProperty("icon").ValueKind).IsEqualTo(System.Text.Json.JsonValueKind.Null);
            await Assert.That(await runtime.ReadCurrentAsync(capture.ObservationId, CancellationToken.None)).IsNotNull();
        }
        await Assert.That(await BinaryContentTests.Count(database.ConnectionString, "contents")).IsEqualTo(0);
    }
}
