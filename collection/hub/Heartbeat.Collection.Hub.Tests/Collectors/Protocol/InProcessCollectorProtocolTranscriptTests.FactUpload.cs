using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Heartbeat.Collection.Hub.Collectors.Packages;
using Heartbeat.Collection.Hub.Collectors.Protocol;
using Heartbeat.Collection.Hub.Collectors.Runtime;
using Heartbeat.Collection.Hub.Configuration;
using Heartbeat.Collection.Hub.Http;
using Heartbeat.Collection.Hub.Upload;
using Heartbeat.Collection.Hub.Tests.Collectors;

namespace Heartbeat.Collection.Hub.Tests.Collectors.Protocol;

public partial class InProcessCollectorProtocolTranscriptTests
{
    [Fact]
    public async Task NativeUpload_RestartRetainsRawFactWithoutLegacyProjection()
    {
        await using var fixture = await ActivatedRuntimeFixture.CreateAsync(new CollectorRuntimeOptions { EnableFactUpload = true });
        var stream = fixture.Activation.Streams["activity"];
        var fact = CreateFact(stream.Descriptor.StreamId);
        var ack = await stream.PublishAsync(Guid.CreateVersion7(), [fact]);
        Assert.Equal(FactDeliveryStatus.Committed, Assert.Single(ack.Results).Status);
        Assert.Empty(fixture.Sink.Segments);
        await fixture.Activation.DisposeAsync();
        fixture.Runtime.Dispose();

        using var restarted = CollectorRuntime.Open(fixture.StatePath, fixture.Sink,
            new CollectorRuntimeOptions { EnableFactUpload = true });
        var item = Assert.Single(restarted.ReadPendingFacts());
        Assert.Equal(fact.FactId, item.Fact!.FactId);
        Assert.True(JsonElement.DeepEquals(fact.Payload, item.Fact.Payload!.Value));
        restarted.ConfirmUploadedFacts([item]);
        Assert.Empty(restarted.ReadPendingFacts());
        restarted.Dispose();
        using var confirmedRestart = CollectorRuntime.Open(fixture.StatePath, fixture.Sink,
            new CollectorRuntimeOptions { EnableFactUpload = true });
        Assert.Empty(confirmedRestart.ReadPendingFacts());
    }

    [Fact]
    public async Task NativeUpload_LateConfirmationCannotConsumeNewRevision()
    {
        await using var fixture = await ActivatedRuntimeFixture.CreateAsync(new CollectorRuntimeOptions { EnableFactUpload = true });
        var stream = fixture.Activation.Streams["activity"];
        var original = CreateFact(stream.Descriptor.StreamId);
        await stream.PublishAsync(Guid.CreateVersion7(), [original]);
        var firstBatch = fixture.Runtime.ReadPendingFacts();
        await stream.PublishAsync(Guid.CreateVersion7(), [original with { Revision = 2, Time = new SegmentFactTime(original.Time.Start!.Value, original.Time.Start.Value.AddSeconds(1), false) }]);

        fixture.Runtime.ConfirmUploadedFacts(firstBatch);

        var updated = Assert.Single(fixture.Runtime.ReadPendingFacts());
        Assert.Equal(2, updated.Fact!.Revision);
        Assert.Equal(original.Time.Start.Value.AddSeconds(1), updated.Fact.End);
        Assert.True(JsonElement.DeepEquals(original.Payload, updated.Fact.Payload!.Value));
        fixture.Runtime.ConfirmUploadedFacts([updated]);
        Assert.True(fixture.Runtime.FactUploadRemainder.IsEmpty);
    }

    [Fact]
    public async Task NativeUpload_OfflineCapacityRetainsPendingAndReclaimsOnlyDeliveredTerminalFacts()
    {
        await using var fixture = await ActivatedRuntimeFixture.CreateAsync(new CollectorRuntimeOptions
        {
            EnableFactUpload = true, MaxDurableFacts = 1
        });
        var stream = fixture.Activation.Streams["activity"];
        var first = CreateFact(stream.Descriptor.StreamId, isFinal: true);
        var second = CreateFact(stream.Descriptor.StreamId, factId: Guid.CreateVersion7(), isFinal: true);
        await stream.PublishAsync(Guid.CreateVersion7(), [first]);
        var retry = await stream.PublishAsync(Guid.CreateVersion7(), [second]);
        Assert.Equal(FactDeliveryStatus.Retry, Assert.Single(retry.Results).Status);
        Assert.Equal(first.FactId, Assert.Single(fixture.Runtime.ReadPendingFacts()).Fact!.FactId);
        fixture.Runtime.ConfirmUploadedFacts(fixture.Runtime.ReadPendingFacts());

        var accepted = await stream.PublishAsync(Guid.CreateVersion7(), [second]);

        Assert.Equal(FactDeliveryStatus.Committed, Assert.Single(accepted.Results).Status);
        Assert.Equal(second.FactId, Assert.Single(fixture.Runtime.ReadPendingFacts()).Fact!.FactId);
        Assert.Single(JsonNode.Parse(File.ReadAllText(fixture.StatePath))!["facts"]!.AsArray());
    }

    [Fact]
    public async Task NativeUpload_GapBlocksRemovalUntilDurablyConfirmed()
    {
        await using var fixture = await ActivatedRuntimeFixture.CreateAsync(new CollectorRuntimeOptions { EnableFactUpload = true });
        var stream = fixture.Activation.Streams["activity"];
        var start = DateTimeOffset.UtcNow.AddMinutes(-1);
        var gap = new StreamGapReport(Guid.CreateVersion7(), start, start.AddSeconds(5), "outbox_overflow", 3);
        await stream.ReportGapAsync(Guid.CreateVersion7(), gap);
        await fixture.Activation.DisposeAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await fixture.Runtime.RemoveInstanceAsync(fixture.Instance.CollectorInstanceId));
        var pending = Assert.Single(fixture.Runtime.ReadPendingFacts());
        Assert.Equal(gap.GapId, pending.Gap!.GapId);
        fixture.Runtime.ConfirmUploadedFacts([pending]);

        await fixture.Runtime.RemoveInstanceAsync(fixture.Instance.CollectorInstanceId);

        Assert.Empty(fixture.Runtime.ReadPendingFacts());
    }

    [Fact]
    public async Task NativeUpload_ConflictIsDurablyQuarantinedAndDoesNotBlockOtherFacts()
    {
        await using var fixture = await ActivatedRuntimeFixture.CreateAsync(new CollectorRuntimeOptions { EnableFactUpload = true });
        var stream = fixture.Activation.Streams["activity"];
        var conflict = CreateFact(stream.Descriptor.StreamId, isFinal: true);
        var good = CreateFact(stream.Descriptor.StreamId, factId: Guid.CreateVersion7(), isFinal: true);
        await stream.PublishAsync(Guid.CreateVersion7(), [conflict, good]);
        var deadLetters = new JsonDeadLetterStore<FactUploadItem>(fixture.StatePath + ".dead-letter.json");
        var delivered = new List<Guid>();
        var upload = new UploadStream<FactUploadItem>("facts", [new RuntimeFactUploadSource(fixture.Runtime)], (batch, _) =>
        {
            if (batch.Any(item => item.Fact!.FactId == conflict.FactId))
                return Task.FromResult(ApiResult.Fail(new HttpResponseMessage(HttpStatusCode.Conflict), "conflict"));
            delivered.AddRange(batch.Select(item => item.Fact!.FactId));
            return Task.FromResult(ApiResult.Ok);
        }, deadLetters);

        await upload.DrainAsync();

        Assert.Equal([good.FactId], delivered);
        Assert.Equal(1, deadLetters.Count);
        Assert.Empty(fixture.Runtime.ReadPendingFacts());
        Assert.Equal(1, upload.Remainder.RetainedLocally);
        Assert.Contains(conflict.FactId.ToString("D"), File.ReadAllText(deadLetters.Location!));
    }

    [Fact]
    public async Task NativeUpload_MachineTransportPreservesExistingHardwareIdentityCasingAndName()
    {
        await using var fixture = await ActivatedRuntimeFixture.CreateAsync(new CollectorRuntimeOptions { EnableFactUpload = true });
        var stream = fixture.Activation.Streams["activity"];
        await stream.PublishAsync(Guid.CreateVersion7(), [CreateFact(stream.Descriptor.StreamId)]);
        var identity = new UploadMachineIdentity(fixture.Instance.Subject.SubjectId.ToString("D").ToUpperInvariant(), "我的 Mac");

        var item = Assert.Single(new RuntimeFactUploadSource(fixture.Runtime, identity).ReadBatch());

        Assert.Equal(identity.HardwareId, item.Stream.Subject.HardwareId);
        Assert.Equal(identity.DeviceName, item.Stream.Subject.DisplayName);
    }

    private sealed record UploadMachineIdentity(string HardwareId, string DeviceName) : IDeviceIdentity;

    [Fact]
    public async Task NativeUpload_OfflineBacklogIsVisibleAndClearsAfterDelivery()
    {
        await using var fixture = await ActivatedRuntimeFixture.CreateAsync(new CollectorRuntimeOptions { EnableFactUpload = true });
        var stream = fixture.Activation.Streams["activity"];
        await stream.PublishAsync(Guid.CreateVersion7(), [CreateFact(stream.Descriptor.StreamId)]);
        var online = false;
        var upload = new UploadStream<FactUploadItem>("facts", [new RuntimeFactUploadSource(fixture.Runtime)], (_, _) =>
            Task.FromResult(online ? ApiResult.Ok : ApiResult.Fail(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable), "offline")));
        await upload.DrainAsync();
        Assert.Equal(UploadStreamState.Backlog, upload.Status.State);
        Assert.Equal(1, upload.Remainder.RetainedLocally);

        online = true;
        await upload.DrainAsync();

        Assert.Equal(UploadStreamState.Ready, upload.Status.State);
        Assert.True(upload.Remainder.IsEmpty);
    }

    [Fact]
    public async Task NativeUpload_LegacyGapWithoutIdIsBackedUpAndAssignedOneDurableIdentity()
    {
        await using var fixture = await ActivatedRuntimeFixture.CreateAsync();
        var stream = fixture.Activation.Streams["activity"];
        var start = DateTimeOffset.UtcNow.AddMinutes(-1);
        await stream.ReportGapAsync(Guid.CreateVersion7(),
            new StreamGapReport(Guid.CreateVersion7(), start, start.AddSeconds(5), "outbox_overflow", 3));
        await fixture.Activation.DisposeAsync();
        fixture.Runtime.Dispose();
        var legacy = JsonNode.Parse(File.ReadAllText(fixture.StatePath))!;
        legacy["schemaVersion"] = 2;
        legacy["gaps"]![0]!.AsObject().Remove("gapId");
        File.WriteAllText(fixture.StatePath, legacy.ToJsonString());
        Guid allocatedId;
        using (var native = CollectorRuntime.Open(fixture.StatePath, fixture.Sink,
                   new CollectorRuntimeOptions { EnableFactUpload = true, MaxDurableFacts = 1 }))
        {
            var item = Assert.Single(native.ReadPendingFacts());
            allocatedId = item.Gap!.GapId;
            Assert.NotEqual(Guid.Empty, allocatedId);
            Assert.Equal(start, item.Gap.Start);
            Assert.True(File.Exists(fixture.StatePath + ".v2.bak"));
        }
        using var restarted = CollectorRuntime.Open(fixture.StatePath, fixture.Sink,
            new CollectorRuntimeOptions { EnableFactUpload = true });
        Assert.Equal(allocatedId, Assert.Single(restarted.ReadPendingFacts()).Gap!.GapId);
    }

    [Fact]
    public async Task NativeUpload_GenericEventRequiresNoInputProjectionAndCannotEvictPendingData()
    {
        using var copy = ReferenceCollectorPackageCopy.Create(ReferencePackagePath);
        var manifest = copy.ReadManifest();
        manifest["supportedCapabilities"]!.AsObject().Remove("facts.segment");
        manifest["supportedCapabilities"]!["facts.event"] = new JsonArray(1);
        manifest["outputs"]![0]!["factKind"] = "event";
        copy.WriteManifest(manifest);
        var package = LocalCollectorPackage.Load(copy.Path);
        using var directory = TemporaryDirectory.Create();
        using var runtime = CollectorRuntime.Open(Path.Combine(directory.Path, "runtime.json"), new RecordingSegmentSink(),
            new CollectorRuntimeOptions { EnableFactUpload = true, MaxDurableFacts = 1 });
        var instance = runtime.CreateInstance(package, new SubjectReference(Guid.CreateVersion7(), SubjectKind.Machine),
            new CollectorInstanceSpec(1, 1, JsonSerializer.SerializeToElement(new { })));
        await using var activation = await runtime.ActivateInProcessAsync(instance.CollectorInstanceId, package,
            new ReferenceInProcessCollector(protocolSupport: new ProtocolSupport([1], new Dictionary<string, IReadOnlyList<int>>
            {
                ["facts.event"] = [1], ["diagnostics.stream-gap"] = [1]
            })));
        var stream = activation.Streams["activity"];
        var first = CreateFact(stream.Descriptor.StreamId) with { Time = new EventFactTime(DateTimeOffset.UtcNow.AddSeconds(-2)) };
        var second = first with { FactId = Guid.CreateVersion7() };
        Assert.Equal(FactDeliveryStatus.Committed,
            Assert.Single((await stream.PublishAsync(Guid.CreateVersion7(), [first])).Results).Status);
        Assert.Equal(FactDeliveryStatus.Retry,
            Assert.Single((await stream.PublishAsync(Guid.CreateVersion7(), [second])).Results).Status);
        runtime.ConfirmUploadedFacts(runtime.ReadPendingFacts());
        Assert.Equal(FactDeliveryStatus.Committed,
            Assert.Single((await stream.PublishAsync(Guid.CreateVersion7(), [second])).Results).Status);
        var item = Assert.Single(runtime.ReadPendingFacts());
        Assert.Equal("event", item.Stream.FactKind);
        Assert.Equal(second.FactId, item.Fact!.FactId);
        Assert.Null(item.Fact.Start);
        Assert.Equal(second.Time.OccurredAt, item.Fact.OccurredAt);
    }

    [Fact]
    public async Task NativeUpload_MigratedGapLostAckBindsAliasOnceWithoutDuplicatingAnalyticsGap()
    {
        await using var fixture = await ActivatedRuntimeFixture.CreateAsync();
        var start = DateTimeOffset.UtcNow.AddMinutes(-1);
        var original = new StreamGapReport(Guid.CreateVersion7(), start, start.AddSeconds(5), "outbox_overflow", 3);
        await fixture.Activation.Streams["activity"].ReportGapAsync(Guid.CreateVersion7(), original);
        await fixture.Activation.DisposeAsync();
        fixture.Runtime.Dispose();
        var legacy = JsonNode.Parse(File.ReadAllText(fixture.StatePath))!;
        legacy["schemaVersion"] = 2;
        legacy["gaps"]![0]!.AsObject().Remove("gapId");
        File.WriteAllText(fixture.StatePath, legacy.ToJsonString());
        using (var native = CollectorRuntime.Open(fixture.StatePath, fixture.Sink,
                   new CollectorRuntimeOptions { EnableFactUpload = true, MaxDurableFacts = 1 }))
        {
            native.ConfirmUploadedFacts(native.ReadPendingFacts());
            await using var activation = await native.ActivateInProcessAsync(fixture.Instance.CollectorInstanceId,
                fixture.Package, new ReferenceInProcessCollector());
            var stream = activation.Streams["activity"];
            Assert.Equal(GapDeliveryStatus.Duplicate,
                (await stream.ReportGapAsync(Guid.CreateVersion7(), original)).Status);
            Assert.Empty(native.ReadPendingFacts());
            // The one legacy match must not swallow another independently identified loss.
            Assert.Equal(GapDeliveryStatus.Committed,
                (await stream.ReportGapAsync(Guid.CreateVersion7(), original with { GapId = Guid.CreateVersion7() })).Status);
            native.ConfirmUploadedFacts(native.ReadPendingFacts());
            Assert.Equal(GapDeliveryStatus.Committed,
                (await stream.ReportGapAsync(Guid.CreateVersion7(), original with { GapId = Guid.CreateVersion7() })).Status);
            native.ConfirmUploadedFacts(native.ReadPendingFacts());
        }
        using var restarted = CollectorRuntime.Open(fixture.StatePath, fixture.Sink,
            new CollectorRuntimeOptions { EnableFactUpload = true });
        await using var resumed = await restarted.ActivateInProcessAsync(fixture.Instance.CollectorInstanceId,
            fixture.Package, new ReferenceInProcessCollector());
        Assert.Equal(GapDeliveryStatus.Duplicate,
            (await resumed.Streams["activity"].ReportGapAsync(Guid.CreateVersion7(), original)).Status);
        Assert.Empty(restarted.ReadPendingFacts());
        Assert.Equal(2, JsonNode.Parse(File.ReadAllText(fixture.StatePath))!["gaps"]!.AsArray().Count);
    }
}
