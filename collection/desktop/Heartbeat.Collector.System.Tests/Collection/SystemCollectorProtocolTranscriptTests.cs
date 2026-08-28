using System.Text.Json;
using System.Text.Json.Nodes;
using Heartbeat.Collector.System.Collection;
using Heartbeat.Collector.System.Configuration;
using Heartbeat.Collector.System.Input;
using Heartbeat.Collector.System.Observations;
using Heartbeat.Collection.Hub.Collectors.Packages;
using Heartbeat.Collection.Hub.Collectors.Protocol;
using Heartbeat.Collection.Hub.Collectors.Runtime;
using Heartbeat.Collection.Hub.Presence;
using Heartbeat.Collection.Hub.Segments;
using Heartbeat.Collection.Hub.Time;
using Heartbeat.Collection.Hub.Upload;
using Heartbeat.Core.DTOs.Input;

namespace Heartbeat.Collector.System.Tests.Collection;

public sealed class SystemCollectorProtocolTranscriptTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"heartbeat-system-protocol-{Guid.NewGuid():N}");

    [Fact]
    public void Package_DeclaresForegroundSegmentAndInputEventOutputs()
    {
        var package = LocalCollectorPackage.Load(SystemCollectorPackage.Path);

        Assert.Collection(
            package.Manifest.Outputs.OrderBy(output => output.OutputId, StringComparer.Ordinal),
            foreground =>
            {
                Assert.Equal("foreground", foreground.OutputId);
                Assert.Equal(FactKind.Segment, foreground.FactKind);
            },
            input =>
            {
                Assert.Equal("input-events", input.OutputId);
                Assert.Equal("system", input.Source);
                Assert.Equal(FactKind.Event, input.FactKind);
                Assert.Equal("heartbeat.input", input.Schema.Id);
            });
    }

    [Fact]
    public async Task PackageUpgrade_AddsEventStreamWithoutChangingCollectorInstance()
    {
        Directory.CreateDirectory(_root);
        var oldPackagePath = Path.Combine(_root, "old-system-package");
        CopyDirectory(SystemCollectorPackage.Path, oldPackagePath);
        var manifestPath = Path.Combine(oldPackagePath, "collector-manifest.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        manifest["version"] = "1.0.0";
        manifest["supportedCapabilities"]!.AsObject().Remove("facts.event");
        var outputs = manifest["outputs"]!.AsArray();
        outputs.Remove(outputs.Single(output => output!["outputId"]!.GetValue<string>() == "input-events"));
        File.WriteAllText(
            manifestPath,
            manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        var oldPackage = LocalCollectorPackage.Load(oldPackagePath);
        var currentPackage = LocalCollectorPackage.Load(SystemCollectorPackage.Path);
        var statePath = Path.Combine(_root, "collector-runtime.json");
        var subject = new SubjectReference(Guid.CreateVersion7(), SubjectKind.Machine);
        Guid instanceId;

        var oldSink = new SegmentIngestService(new FakeClock());
        await using (var oldRuntime = CollectorRuntime.Open(statePath, oldSink))
        {
            using var config = JsonDocument.Parse("{}");
            instanceId = oldRuntime.CreateInstance(
                oldPackage,
                subject,
                new CollectorInstanceSpec(1, 1, config.RootElement.Clone())).CollectorInstanceId;
        }

        var clock = new FakeClock();
        var segmentSink = new SegmentIngestService(clock);
        var inputSink = new CapturingInputEventSink();
        await using var runtime = CollectorRuntime.Open(
            statePath,
            segmentSink,
            inputEventSink: inputSink);
        var protocol = new SystemCollectorProtocolAdapter();
        await using var activation = await runtime.ActivateInProcessAsync(
            instanceId,
            currentPackage,
            NewCollector(protocol, clock, segmentSink));

        Assert.Equal(instanceId, runtime.GetInstance(instanceId).CollectorInstanceId);
        Assert.Equal("1.1.1", runtime.GetInstance(instanceId).PackageVersion);
        Assert.Equal(2, activation.Streams.Count);
    }

    [Fact]
    public async Task ForegroundObservation_UsesReferenceProtocolTranscript_AndGrowsFullSnapshots()
    {
        Directory.CreateDirectory(_root);
        var clock = new FakeClock();
        var sink = new SegmentIngestService(clock);
        var protocol = new SystemCollectorProtocolAdapter();
        var observations = new FakeObservations
        {
            CurrentActivity = new DesktopActivity("win:code", "Code", "main.cs")
        };
        var monitor = new AppMonitorService(
            clock,
            observations,
            new FakeInteractionSignal(),
            protocol,
            sink,
            new FakeSettings());
        var collector = new SystemInProcessCollector(protocol, monitor);
        var package = LocalCollectorPackage.Load(SystemCollectorPackage.Path);
        using var config = JsonDocument.Parse("{}");
        await using var runtime = CollectorRuntime.Open(
            Path.Combine(_root, "collector-runtime.json"),
            sink,
            inputEventSink: new CapturingInputEventSink());
        var instance = runtime.CreateInstance(
            package,
            new SubjectReference(
                Guid.Parse("0198d5df-5df3-70a1-937d-68a7d64623e2"),
                SubjectKind.Machine),
            new CollectorInstanceSpec(1, 1, config.RootElement.Clone()));

        await using var activation = await runtime.ActivateInProcessAsync(
            instance.CollectorInstanceId,
            package,
            collector);

        Assert.Equal(CollectorActivationState.Ready, activation.State);
        Assert.Equal(
            [
                CollectorHandshakeStep.Hello,
                CollectorHandshakeStep.Initialize,
                CollectorHandshakeStep.StreamsOpen,
                CollectorHandshakeStep.Ready
            ],
            activation.HandshakeTranscript);
        var stream = activation.Streams[SystemInProcessCollector.ForegroundBindingId].Descriptor;
        Assert.Equal("foreground", stream.OutputId);
        Assert.Equal("system", stream.Source);
        Assert.Equal(FactKind.Segment, stream.FactKind);
        Assert.Equal("heartbeat.system.foreground-segment", stream.Schema.Id);
        var inputStream = activation.Streams[SystemInProcessCollector.InputEventBindingId].Descriptor;
        Assert.Equal("input-events", inputStream.OutputId);
        Assert.Equal(FactKind.Event, inputStream.FactKind);
        Assert.Equal("heartbeat.input", inputStream.Schema.Id);

        clock.Advance(TimeSpan.FromSeconds(30));
        monitor.PushCurrentSnapshot();
        var first = Assert.Single(sink.GetAndClearSegments());
        Assert.Equal("system", first.Source);
        Assert.Equal("win:code", first.AppIdentityKey);
        Assert.Equal("Code", first.AppDisplayName);
        Assert.Equal("main.cs", first.Title);
        Assert.Null(first.AppName);
        Assert.Null(first.Attributes);
        Assert.Equal(DateTimeOffset.UnixEpoch, first.StartTime);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(30), first.EndTime);

        clock.Advance(TimeSpan.FromSeconds(30));
        observations.Activate("win:chrome", "Docs", "Chrome");
        var grown = Assert.Single(sink.GetAndClearSegments());
        Assert.Equal(first.Id, grown.Id);
        Assert.Equal(first.StartTime, grown.StartTime);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(60), grown.EndTime);
        Assert.Equal("win:chrome", sink.CurrentActivity!.AppIdentityKey);
        Assert.True(sink.SourceLastSeen.ContainsKey("system"));
    }

    [Fact]
    public async Task InputObservation_UsesEventFactAndProjectsToExistingUploadItem()
    {
        Directory.CreateDirectory(_root);
        var clock = new FakeClock();
        var segmentSink = new SegmentIngestService(clock);
        var protocol = new SystemCollectorProtocolAdapter();
        var inputBuffer = new InputEventBuffer(clock, publisher: protocol);
        var monitor = new AppMonitorService(
            clock,
            new FakeObservations(),
            new FakeInteractionSignal(),
            protocol,
            segmentSink,
            new FakeSettings());
        var collector = new SystemInProcessCollector(protocol, monitor);
        var package = LocalCollectorPackage.Load(SystemCollectorPackage.Path);
        using var config = JsonDocument.Parse("{}");
        await using var runtime = CollectorRuntime.Open(
            Path.Combine(_root, "collector-runtime.json"),
            segmentSink,
            inputEventSink: inputBuffer);
        var instance = runtime.CreateInstance(
            package,
            new SubjectReference(Guid.CreateVersion7(), SubjectKind.Machine),
            new CollectorInstanceSpec(1, 1, config.RootElement.Clone()));
        await using var activation = await runtime.ActivateInProcessAsync(
            instance.CollectorInstanceId,
            package,
            collector);

        clock.Advance(TimeSpan.FromMilliseconds(225));
        Assert.True(inputBuffer.OnKeyDown(InputKeyPosition.KeyA));
        await WaitUntilAsync(() => inputBuffer.Count == 1);

        var projected = Assert.Single(inputBuffer.DrainAll());
        Assert.Equal(7, int.Parse(projected.Id.ToString("D")[14].ToString()));
        Assert.Equal(InputEventType.KeyDown, projected.EventType);
        Assert.Equal(InputCodeSets.HeartbeatKeyPositionV1, projected.CodeSet);
        Assert.Equal((short)InputKeyPosition.KeyA, projected.Code);
        Assert.Equal(clock.UtcNow, projected.Timestamp);
        Assert.Equal(2, activation.Streams.Count);
    }

    [Fact]
    public async Task EventReplay_IsIdempotent_AndHigherPresentRevisionIsRejected()
    {
        Directory.CreateDirectory(_root);
        var clock = new FakeClock();
        var segmentSink = new SegmentIngestService(clock);
        var inputSink = new CapturingInputEventSink();
        var protocol = new SystemCollectorProtocolAdapter();
        var monitor = new AppMonitorService(
            clock,
            new FakeObservations(),
            new FakeInteractionSignal(),
            protocol,
            segmentSink,
            new FakeSettings());
        var collector = new SystemInProcessCollector(protocol, monitor);
        var package = LocalCollectorPackage.Load(SystemCollectorPackage.Path);
        using var config = JsonDocument.Parse("{}");
        await using var runtime = CollectorRuntime.Open(
            Path.Combine(_root, "collector-runtime.json"),
            segmentSink,
            inputEventSink: inputSink);
        var instance = runtime.CreateInstance(
            package,
            new SubjectReference(Guid.CreateVersion7(), SubjectKind.Machine),
            new CollectorInstanceSpec(1, 1, config.RootElement.Clone()));
        await using var activation = await runtime.ActivateInProcessAsync(
            instance.CollectorInstanceId,
            package,
            collector);
        var stream = activation.Streams[SystemInProcessCollector.InputEventBindingId];
        var factId = Guid.CreateVersion7();
        var fact = new FactSubmission(
            stream.Descriptor.StreamId,
            stream.Descriptor.Schema.Revision,
            factId,
            Revision: 1,
            ObservedAt: null,
            FactRecordState.Present,
            new EventFactTime(DateTimeOffset.UnixEpoch),
            JsonSerializer.SerializeToElement(new
            {
                eventType = "keyDown",
                codeSet = InputCodeSets.HeartbeatKeyPositionV1,
                code = (short)InputKeyPosition.KeyA
            }));

        var first = await stream.PublishAsync(Guid.CreateVersion7(), [fact]);
        var replay = await stream.PublishAsync(Guid.CreateVersion7(), [fact]);
        var higher = await stream.PublishAsync(
            Guid.CreateVersion7(),
            [fact with { Revision = 2 }]);

        Assert.Equal(FactDeliveryStatus.Committed, Assert.Single(first.Results).Status);
        Assert.Equal(FactDeliveryStatus.Duplicate, Assert.Single(replay.Results).Status);
        var rejected = Assert.Single(higher.Results);
        Assert.Equal(FactDeliveryStatus.Rejected, rejected.Status);
        Assert.Equal("fact_schema_invalid", rejected.Error?.Code);
        var projected = Assert.Single(inputSink.Items);
        Assert.Equal(factId, projected.Id);
    }

    [Fact]
    public async Task CommittedEvent_IsReplayedAfterHubRestart_AndRetryDoesNotDuplicateProjection()
    {
        Directory.CreateDirectory(_root);
        var statePath = Path.Combine(_root, "collector-runtime.json");
        var package = LocalCollectorPackage.Load(SystemCollectorPackage.Path);
        var factId = Guid.CreateVersion7();
        Guid instanceId;
        FactSubmission fact;

        var firstClock = new FakeClock();
        var firstSegmentSink = new SegmentIngestService(firstClock);
        var firstInputSink = new CapturingInputEventSink();
        await using (var firstRuntime = CollectorRuntime.Open(
                         statePath,
                         firstSegmentSink,
                         inputEventSink: firstInputSink))
        {
            using var config = JsonDocument.Parse("{}");
            var instance = firstRuntime.CreateInstance(
                package,
                new SubjectReference(Guid.CreateVersion7(), SubjectKind.Machine),
                new CollectorInstanceSpec(1, 1, config.RootElement.Clone()));
            instanceId = instance.CollectorInstanceId;
            var firstProtocol = new SystemCollectorProtocolAdapter();
            await using var firstActivation = await firstRuntime.ActivateInProcessAsync(
                instanceId,
                package,
                NewCollector(firstProtocol, firstClock, firstSegmentSink));
            var stream = firstActivation.Streams[SystemInProcessCollector.InputEventBindingId];
            fact = InputFact(stream.Descriptor, factId);

            var committed = await stream.PublishAsync(Guid.CreateVersion7(), [fact]);

            Assert.Equal(FactDeliveryStatus.Committed, Assert.Single(committed.Results).Status);
            Assert.Single(firstInputSink.Items);
        }

        var recoveredClock = new FakeClock();
        var recoveredSegmentSink = new SegmentIngestService(recoveredClock);
        var recoveredInputSink = new CapturingInputEventSink();
        await using var recoveredRuntime = CollectorRuntime.Open(
            statePath,
            recoveredSegmentSink,
            inputEventSink: recoveredInputSink);
        var recoveredProtocol = new SystemCollectorProtocolAdapter();
        await using var recoveredActivation = await recoveredRuntime.ActivateInProcessAsync(
            instanceId,
            package,
            NewCollector(recoveredProtocol, recoveredClock, recoveredSegmentSink));

        var replay = await recoveredActivation.Streams[SystemInProcessCollector.InputEventBindingId]
            .PublishAsync(Guid.CreateVersion7(), [fact]);

        Assert.Equal(FactDeliveryStatus.Duplicate, Assert.Single(replay.Results).Status);
        Assert.Single(recoveredInputSink.Items);
        Assert.Equal(factId, recoveredInputSink.Items[0].Id);
    }

    [Fact]
    public async Task InputEvent_DurableWindowAtCapacity_EvictsOldReceiptInsteadOfStallingStream()
    {
        Directory.CreateDirectory(_root);
        var clock = new FakeClock();
        var segmentSink = new SegmentIngestService(clock);
        var inputSink = new CapturingInputEventSink();
        var protocol = new SystemCollectorProtocolAdapter();
        var package = LocalCollectorPackage.Load(SystemCollectorPackage.Path);
        using var config = JsonDocument.Parse("{}");
        await using var runtime = CollectorRuntime.Open(
            Path.Combine(_root, "collector-runtime.json"),
            segmentSink,
            new CollectorRuntimeOptions { MaxDurableFacts = 1 },
            inputEventSink: inputSink);
        var instance = runtime.CreateInstance(
            package,
            new SubjectReference(Guid.CreateVersion7(), SubjectKind.Machine),
            new CollectorInstanceSpec(1, 1, config.RootElement.Clone()));
        await using var activation = await runtime.ActivateInProcessAsync(
            instance.CollectorInstanceId,
            package,
            NewCollector(protocol, clock, segmentSink));
        var stream = activation.Streams[SystemInProcessCollector.InputEventBindingId];

        var first = await stream.PublishAsync(
            Guid.CreateVersion7(),
            [InputFact(stream.Descriptor, Guid.CreateVersion7())]);
        var second = await stream.PublishAsync(
            Guid.CreateVersion7(),
            [InputFact(stream.Descriptor, Guid.CreateVersion7())]);

        Assert.Equal(FactDeliveryStatus.Committed, Assert.Single(first.Results).Status);
        Assert.Equal(FactDeliveryStatus.Committed, Assert.Single(second.Results).Status);
        Assert.Equal(2, inputSink.Items.Count);
    }

    [Fact]
    public async Task InputEvent_DurableProjectionFailure_ReturnsRetryWithoutCommittingReceipt()
    {
        Directory.CreateDirectory(_root);
        var statePath = Path.Combine(_root, "collector-runtime.json");
        var clock = new FakeClock();
        var segmentSink = new SegmentIngestService(clock);
        var protocol = new SystemCollectorProtocolAdapter();
        var package = LocalCollectorPackage.Load(SystemCollectorPackage.Path);
        using var config = JsonDocument.Parse("{}");
        await using var runtime = CollectorRuntime.Open(
            statePath,
            segmentSink,
            inputEventSink: new ThrowingInputEventSink());
        var instance = runtime.CreateInstance(
            package,
            new SubjectReference(Guid.CreateVersion7(), SubjectKind.Machine),
            new CollectorInstanceSpec(1, 1, config.RootElement.Clone()));
        await using var activation = await runtime.ActivateInProcessAsync(
            instance.CollectorInstanceId,
            package,
            NewCollector(protocol, clock, segmentSink));
        var stream = activation.Streams[SystemInProcessCollector.InputEventBindingId];

        var acknowledgement = await stream.PublishAsync(
            Guid.CreateVersion7(),
            [InputFact(stream.Descriptor, Guid.CreateVersion7())]);

        Assert.Equal(FactDeliveryStatus.Retry, Assert.Single(acknowledgement.Results).Status);
        using var state = JsonDocument.Parse(File.ReadAllText(statePath));
        Assert.Empty(state.RootElement.GetProperty("facts").EnumerateArray());
    }

    [Fact]
    public void InputHookPublication_OnlyQueuesAndDoesNotRequireAnOpenedProtocolStream()
    {
        var protocol = new SystemCollectorProtocolAdapter();
        var buffer = new InputEventBuffer(new FakeClock(), publisher: protocol);

        buffer.OnMouseButton(1);

        Assert.Equal(0, buffer.Count);
    }

    [Fact]
    public async Task SnapshotPublisher_AssignsStableFactIdAndMonotonicRevision()
    {
        var x = BuildScenario("mac:com.apple.Terminal", "shell", "Terminal");

        x.Clock.Advance(TimeSpan.FromSeconds(30));
        x.Service.PushCurrentSnapshot();
        x.Clock.Advance(TimeSpan.FromSeconds(30));
        x.Service.PushCurrentSnapshot();

        Assert.Collection(
            x.Publisher.Snapshots,
            first =>
            {
                Assert.Equal(1, first.Revision);
                Assert.False(first.IsFinal);
            },
            second =>
            {
                Assert.Equal(2, second.Revision);
                Assert.False(second.IsFinal);
                Assert.Equal(x.Publisher.Snapshots[0].FactId, second.FactId);
                Assert.Equal(x.Publisher.Snapshots[0].Start, second.Start);
                Assert.True(second.End > x.Publisher.Snapshots[0].End);
            });

        await x.Service.StopAsync(CancellationToken.None);
        var final = x.Publisher.Snapshots[^1];
        Assert.Equal(3, final.Revision);
        Assert.True(final.IsFinal);
        Assert.Equal(x.Publisher.Snapshots[0].FactId, final.FactId);
    }

    private static Scenario BuildScenario(
        string appIdentityKey,
        string title,
        string displayName)
    {
        var clock = new FakeClock();
        var observations = new FakeObservations
        {
            CurrentActivity = new DesktopActivity(appIdentityKey, displayName, title)
        };
        var publisher = new CapturingPublisher();
        var activity = new CapturingActivity();
        var service = new AppMonitorService(
            clock,
            observations,
            new FakeInteractionSignal(),
            publisher,
            activity,
            new FakeSettings());
        service.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        return new Scenario(service, clock, publisher);
    }

    private static SystemInProcessCollector NewCollector(
        SystemCollectorProtocolAdapter protocol,
        IClock clock,
        SegmentIngestService segmentSink) => new(
        protocol,
        new AppMonitorService(
            clock,
            new FakeObservations(),
            new FakeInteractionSignal(),
            protocol,
            segmentSink,
            new FakeSettings()));

    private static FactSubmission InputFact(FactStreamDescriptor descriptor, Guid factId) => new(
        descriptor.StreamId,
        descriptor.Schema.Revision,
        factId,
        Revision: 1,
        ObservedAt: null,
        FactRecordState.Present,
        new EventFactTime(DateTimeOffset.UnixEpoch),
        JsonSerializer.SerializeToElement(new
        {
            eventType = "keyDown",
            codeSet = InputCodeSets.HeartbeatKeyPositionV1,
            code = (short)InputKeyPosition.KeyA
        }));

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }

    private static void CopyDirectory(string source, string destination)
    {
        foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(directory.Replace(source, destination, StringComparison.Ordinal));
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace(source, destination, StringComparison.Ordinal));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private sealed record Scenario(
        AppMonitorService Service,
        FakeClock Clock,
        CapturingPublisher Publisher);

    private sealed class CapturingPublisher : ISystemSegmentPublisher
    {
        public List<ForegroundSegmentSnapshot> Snapshots { get; } = [];
        public void Publish(ForegroundSegmentSnapshot snapshot) => Snapshots.Add(snapshot);
    }

    private sealed class CapturingActivity : ICurrentActivitySink
    {
        public void Report(CurrentActivity? activity) { }
    }

    private sealed class CapturingInputEventSink : IInputEventFactSink
    {
        public List<InputEventItem> Items { get; } = [];

        public void Accept(InputEventItem item, bool isReplay) => Items.Add(item);
    }

    private sealed class ThrowingInputEventSink : IInputEventFactSink
    {
        public void Accept(InputEventItem item, bool isReplay) =>
            throw new IOException("durable projection unavailable");
    }

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = DateTimeOffset.UnixEpoch;
        public void Advance(TimeSpan duration) => UtcNow += duration;
    }

    private sealed class FakeObservations : IDesktopObservationSource
    {
        public event Action<DesktopObservation>? Observation;
        public DesktopActivity CurrentActivity { get; set; } = DesktopActivity.None;
        public void Start() { }
        public void Stop() { }

        public void Activate(string? appIdentityKey, string? title, string? displayName)
        {
            CurrentActivity = new DesktopActivity(appIdentityKey, displayName, title);
            Observation?.Invoke(DesktopObservation.AppActivated(CurrentActivity));
        }
    }

    private sealed class FakeInteractionSignal : IInputActivitySignal
    {
        public void MarkClick() { }
        public bool ClickedWithin(TimeSpan window) => false;
    }

    private sealed class FakeSettings : IDesktopSettings
    {
        public IReadOnlyList<string> AwayProcessNames => [];
        public bool SplitFocusedWindowChangesUnconditionally => true;
        public event Action<IReadOnlyList<string>>? AwayProcessNamesChanged
        {
            add { }
            remove { }
        }
    }
}
