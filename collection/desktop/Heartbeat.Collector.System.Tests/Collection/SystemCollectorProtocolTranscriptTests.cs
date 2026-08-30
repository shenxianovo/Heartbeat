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

        var declaration = Assert.IsType<VerifiedObservationDeclaration>(package.ObservationDeclaration);
        Assert.Equal("system", declaration.Source);
        Assert.Equal(1, declaration.Version);
        Assert.Contains("\"app\"", declaration.Json);

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
        var first = Assert.Single(await WaitForSegmentsAsync(sink));
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
        var grown = Assert.Single(await WaitForSegmentsAsync(sink));
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
    public async Task InputEvent_DurableProjectionAtCapacity_RetriesSameFactUntilConfirmedSpaceExists()
    {
        Directory.CreateDirectory(_root);
        var clock = new FakeClock();
        var segmentSink = new SegmentIngestService(clock);
        var projectionPath = Path.Combine(_root, "input-event-facts-buffer.json");
        var inputSink = new InputEventBuffer(clock, capacity: 1, durableProjectionPath: projectionPath);
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
        var secondFact = InputFact(stream.Descriptor, Guid.CreateVersion7());
        var second = await stream.PublishAsync(Guid.CreateVersion7(), [secondFact]);

        Assert.Equal(FactDeliveryStatus.Committed, Assert.Single(first.Results).Status);
        Assert.Equal(FactDeliveryStatus.Retry, Assert.Single(second.Results).Status);
        var drained = ((IUploadSource<InputEventItem>)inputSink).Drain();
        Assert.Single(drained);
        ((IDurableUploadSource<InputEventItem>)inputSink).CompleteDrain(drained, []);

        var retried = await stream.PublishAsync(Guid.CreateVersion7(), [secondFact]);

        Assert.Equal(FactDeliveryStatus.Committed, Assert.Single(retried.Results).Status);
        Assert.Equal(secondFact.FactId, Assert.Single(inputSink.DrainAll()).Id);
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
    public async Task ForegroundObservation_ReturnsWhileProtocolDeliveryIsBackpressured()
    {
        Directory.CreateDirectory(_root);
        var clock = new FakeClock();
        var segmentSink = new SegmentIngestService(clock);
        var inputSink = new BlockingInputEventSink();
        var protocol = new SystemCollectorProtocolAdapter();
        var inputBuffer = new InputEventBuffer(clock, publisher: protocol);
        var observations = new FakeObservations
        {
            CurrentActivity = new DesktopActivity("mac:com.apple.Terminal", "Terminal", "shell")
        };
        var monitor = new AppMonitorService(
            clock,
            observations,
            new FakeInteractionSignal(),
            protocol,
            segmentSink,
            new FakeSettings());
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
            new SystemInProcessCollector(protocol, monitor));

        inputBuffer.OnMouseButton(1);
        Assert.True(inputSink.Entered.Wait(TimeSpan.FromSeconds(2)));
        clock.Advance(TimeSpan.FromSeconds(30));
        using var observationReturned = new ManualResetEventSlim();
        var observationThread = new Thread(() =>
        {
            observations.Activate("mac:com.google.Chrome", "Docs", "Chrome");
            observationReturned.Set();
        })
        {
            IsBackground = true,
            Name = "Blocked desktop observation fixture"
        };

        observationThread.Start();
        var returnedWhileDeliveryBlocked = observationReturned.Wait(TimeSpan.FromSeconds(2));
        inputSink.Release();
        Assert.True(observationReturned.Wait(TimeSpan.FromSeconds(2)));

        Assert.True(
            returnedWhileDeliveryBlocked,
            "Desktop observation synchronously waited for Collector Protocol delivery.");
    }

    [Fact]
    public async Task InputIngressOverflow_AtomicallyStagesGapAndUploadsItAfterBackpressureClears()
    {
        Directory.CreateDirectory(_root);
        var statePath = Path.Combine(_root, "collector-runtime.json");
        var clock = new FakeClock();
        var segmentSink = new SegmentIngestService(clock);
        var inputSink = new BlockingInputEventSink();
        var statuses = new UploadStatusRegistry();
        var protocol = new SystemCollectorProtocolAdapter(
            statuses,
            inputEventIngressCapacity: 1);
        var inputBuffer = new InputEventBuffer(clock, publisher: protocol);
        var package = LocalCollectorPackage.Load(SystemCollectorPackage.Path);
        using var config = JsonDocument.Parse("{}");
        await using var runtime = CollectorRuntime.Open(
            statePath,
            segmentSink,
            inputEventSink: inputSink);
        var instance = runtime.CreateInstance(
            package,
            new SubjectReference(Guid.CreateVersion7(), SubjectKind.Machine),
            new CollectorInstanceSpec(1, 1, config.RootElement.Clone()));
        await using var activation = await runtime.ActivateInProcessAsync(
            instance.CollectorInstanceId,
            package,
            NewCollector(protocol, clock, segmentSink));

        inputBuffer.OnMouseButton(1);
        Assert.True(inputSink.Entered.Wait(TimeSpan.FromSeconds(2)));
        clock.Advance(TimeSpan.FromSeconds(1));
        inputBuffer.OnMouseButton(2);
        clock.Advance(TimeSpan.FromSeconds(1));
        inputBuffer.OnMouseButton(3);

        inputSink.Release();
        await WaitUntilAsync(() => RuntimeHasGap(statePath, "input_ingress_capacity_exceeded"));
        Assert.Equal(
            UploadStreamState.Ready,
            statuses.Snapshot[SystemCollectorProtocolAdapter.StatusStreamName].State);
        Assert.True(
            RuntimeHasGap(statePath, "input_ingress_capacity_exceeded"),
            File.ReadAllText(statePath));
    }

    [Fact]
    public async Task NativeInputCallbackDoesNotWaitForIngressJournalPersistence()
    {
        Directory.CreateDirectory(_root);
        var statePath = Path.Combine(_root, "collector-runtime.json");
        var ingressPath = Path.Combine(_root, "system-collector-ingress.json");
        var clock = new FakeClock();
        var segmentSink = new SegmentIngestService(clock);
        var inputSink = new BlockingInputEventSink();
        var protocol = new SystemCollectorProtocolAdapter(inputEventIngressCapacity: 2);
        var inputBuffer = new InputEventBuffer(clock, publisher: protocol);
        var package = LocalCollectorPackage.Load(SystemCollectorPackage.Path);
        using var config = JsonDocument.Parse("{}");
        await using var runtime = CollectorRuntime.Open(
            statePath,
            segmentSink,
            inputEventSink: inputSink);
        var instance = runtime.CreateInstance(
            package,
            new SubjectReference(Guid.CreateVersion7(), SubjectKind.Machine),
            new CollectorInstanceSpec(1, 1, config.RootElement.Clone()));
        await using var activation = await runtime.ActivateInProcessAsync(
            instance.CollectorInstanceId,
            package,
            NewCollector(protocol, clock, segmentSink));

        inputBuffer.OnMouseButton(1);
        Assert.True(inputSink.Entered.Wait(TimeSpan.FromSeconds(2)));
        if (File.Exists(ingressPath))
            File.Delete(ingressPath);
        Directory.CreateDirectory(ingressPath);
        Exception? callbackFailure = null;
        using var callbackReturned = new ManualResetEventSlim();
        var callbackThread = new Thread(() =>
        {
            try
            {
                inputBuffer.OnMouseButton(2);
            }
            catch (Exception exception)
            {
                callbackFailure = exception;
            }
            finally
            {
                callbackReturned.Set();
            }
        }) { IsBackground = true };

        callbackThread.Start();
        var returned = callbackReturned.Wait(TimeSpan.FromSeconds(2));
        Directory.Delete(ingressPath);
        inputSink.Release();

        Assert.True(returned, "Native InputEvent callback waited for ingress journal persistence.");
        Assert.Null(callbackFailure);
    }

    [Fact]
    public async Task DrainDeadlineStagesSystemIngressTailAndRestartReplaysDurableRemainder()
    {
        Directory.CreateDirectory(_root);
        var statePath = Path.Combine(_root, "collector-runtime.json");
        var clock = new FakeClock();
        var package = LocalCollectorPackage.Load(SystemCollectorPackage.Path);
        using var config = JsonDocument.Parse("{}");
        Guid instanceId;
        string outboxPath;
        string ingressPath;
        var blockedSink = new BlockingInputEventSink();

        var runtime = CollectorRuntime.Open(
            statePath,
            new SegmentIngestService(clock),
            new CollectorRuntimeOptions
            {
                InProcessDrainGracePeriod = TimeSpan.FromMilliseconds(200)
            },
            inputEventSink: blockedSink);
        try
        {
            var instance = runtime.CreateInstance(
                package,
                new SubjectReference(Guid.CreateVersion7(), SubjectKind.Machine),
                new CollectorInstanceSpec(1, 1, config.RootElement.Clone()));
            instanceId = instance.CollectorInstanceId;
            outboxPath = Path.Combine(
                _root,
                "collector-data",
                instanceId.ToString("N"),
                "collector-protocol-outbox.json");
            ingressPath = Path.Combine(
                _root,
                "collector-data",
                instanceId.ToString("N"),
                "system-collector-ingress.json");
            var protocol = new SystemCollectorProtocolAdapter();
            var collector = NewCollector(protocol, clock, new SegmentIngestService(clock));
            var activation = await runtime.ActivateInProcessAsync(
                instanceId,
                package,
                collector);

            for (var index = 0; index < 100; index++)
            {
                protocol.Publish(new InputEventItem
                {
                    Id = Guid.CreateVersion7(),
                    EventType = InputEventType.MouseButton,
                    CodeSet = InputCodeSets.HeartbeatKeyPositionV1,
                    Code = (short)(index % 3 + 1),
                    Timestamp = clock.UtcNow.AddTicks(index)
                });
            }
            Assert.True(blockedSink.Entered.Wait(TimeSpan.FromSeconds(2)));
            Assert.Equal(100, DurableFactIds(statePath, outboxPath, ingressPath).Count);

            var stopTask = activation.StopAsync().AsTask();
            await stopTask.WaitAsync(TimeSpan.FromSeconds(2));
            var hubStateAfterStop = File.ReadAllText(statePath);
            blockedSink.Release();

            Assert.Equal(CollectorDrainReason.DeadlineExceeded, activation.DrainResult!.LogicalResult.Reason);
            var durableRemainder = OutboxFactCount(outboxPath);
            Assert.Equal(100, DurableFactIds(statePath, outboxPath, ingressPath).Count);
            await Task.Delay(50);
            Assert.Equal(durableRemainder, OutboxFactCount(outboxPath));
            Assert.Equal(hubStateAfterStop, File.ReadAllText(statePath));
        }
        finally
        {
            blockedSink.Release();
            await runtime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        }

        var replaySink = new CapturingInputEventSink();
        await using (var restarted = CollectorRuntime.Open(
            statePath,
            new SegmentIngestService(clock),
            inputEventSink: replaySink))
        {
            var protocol = new SystemCollectorProtocolAdapter();
            await using var activation = await restarted.ActivateInProcessAsync(
                instanceId,
                package,
                NewCollector(protocol, clock, new SegmentIngestService(clock)));
            await WaitUntilAsync(() => OutboxFactCount(outboxPath) == 0 && IngressFactCount(ingressPath) == 0);
        }

        using var state = JsonDocument.Parse(File.ReadAllText(statePath));
        Assert.Equal(100, state.RootElement.GetProperty("facts").GetArrayLength());
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

    private static bool RuntimeHasGap(string statePath, string reason)
    {
        try
        {
            using var state = JsonDocument.Parse(File.ReadAllText(statePath));
            return state.RootElement.GetProperty("gaps").EnumerateArray().Any(gap =>
                gap.GetProperty("reason").GetString() == reason);
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return false;
        }
    }

    private static int OutboxFactCount(string outboxPath)
    {
        try
        {
            using var outbox = JsonDocument.Parse(File.ReadAllText(outboxPath));
            return outbox.RootElement.GetProperty("State").GetProperty("Facts").GetArrayLength();
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return -1;
        }
    }

    private static int IngressFactCount(string ingressPath)
        => SystemCollectorIngressStore.Open(ingressPath, 100_000).PendingFactIds.Count;

    private static HashSet<Guid> DurableFactIds(string statePath, string outboxPath, string ingressPath)
    {
        using var state = JsonDocument.Parse(File.ReadAllText(statePath));
        using var outbox = JsonDocument.Parse(File.ReadAllText(outboxPath));
        var ingress = SystemCollectorIngressStore.Open(ingressPath, 100_000);
        return state.RootElement.GetProperty("facts").EnumerateArray()
            .Select(item => item.GetProperty("factId").GetGuid())
            .Concat(outbox.RootElement.GetProperty("State").GetProperty("Facts").EnumerateArray()
                .Select(item => item.GetProperty("Fact").GetProperty("FactId").GetGuid()))
            .Concat(ingress.PendingFactIds)
            .ToHashSet();
    }

    private static async Task<List<Heartbeat.Core.DTOs.Segments.ActivitySegmentItem>> WaitForSegmentsAsync(
        SegmentIngestService sink)
    {
        List<Heartbeat.Core.DTOs.Segments.ActivitySegmentItem> segments = [];
        await WaitUntilAsync(() =>
        {
            segments = sink.GetAndClearSegments();
            return segments.Count != 0;
        });
        return segments;
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
        public void StageDurableBatch(IReadOnlyList<ForegroundSegmentSnapshot> snapshots) =>
            Snapshots.AddRange(snapshots);
        public void RecoverInterruptedSegment(DateTimeOffset recoveredAt) { }
        public void ClearActiveCheckpoint(Guid factId, long revision) { }
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

    private sealed class BlockingInputEventSink : IInputEventFactSink
    {
        private readonly ManualResetEventSlim _release = new();

        public ManualResetEventSlim Entered { get; } = new();

        public void Accept(InputEventItem item, bool isReplay)
        {
            Entered.Set();
            _release.Wait(TimeSpan.FromMilliseconds(1_500));
        }

        public void Release() => _release.Set();
    }

    private sealed class ControlledTimeProvider : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ControlledTimer> _timers = [];
        private DateTimeOffset _utcNow = new(2026, 8, 30, 8, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
                return _utcNow;
        }

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            var timer = new ControlledTimer(this, callback, state, dueTime, period);
            lock (_gate)
                _timers.Add(timer);
            return timer;
        }

        public void Advance(TimeSpan duration)
        {
            ControlledTimer[] due;
            lock (_gate)
            {
                _utcNow += duration;
                due = _timers.Where(timer => timer.IsDue(_utcNow)).ToArray();
            }
            foreach (var timer in due)
                timer.Fire();
        }

        private sealed class ControlledTimer(
            ControlledTimeProvider owner,
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period) : ITimer
        {
            private DateTimeOffset _dueAt = owner.GetUtcNow() + dueTime;
            private bool _disposed;

            public bool IsDue(DateTimeOffset now) => !_disposed && now >= _dueAt;

            public void Fire()
            {
                if (_disposed)
                    return;
                if (period == Timeout.InfiniteTimeSpan)
                    _disposed = true;
                else
                    _dueAt += period;
                callback(state);
            }

            public bool Change(TimeSpan newDueTime, TimeSpan newPeriod)
            {
                if (_disposed)
                    return false;
                dueTime = newDueTime;
                period = newPeriod;
                _dueAt = owner.GetUtcNow() + newDueTime;
                return true;
            }

            public void Dispose() => _disposed = true;

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
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
