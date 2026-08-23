using System.Text.Json;
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

namespace Heartbeat.Collector.System.Tests.Collection;

public sealed class SystemCollectorProtocolTranscriptTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"heartbeat-system-protocol-{Guid.NewGuid():N}");

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
            sink);
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
        var stream = Assert.Single(activation.Streams).Value.Descriptor;
        Assert.Equal("foreground", stream.OutputId);
        Assert.Equal("system", stream.Source);
        Assert.Equal(FactKind.Segment, stream.FactKind);
        Assert.Equal("heartbeat.system.foreground-segment", stream.Schema.Id);

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

    [Fact]
    public async Task HubRetry_RetainsLatestFullSnapshotInDurableCollectorOutbox()
    {
        Directory.CreateDirectory(_root);
        var outboxPath = Path.Combine(_root, "system-collector-outbox.json");
        var clock = new FakeClock();
        var sink = new SegmentIngestService(clock);
        var protocol = new SystemCollectorProtocolAdapter();
        protocol.ConfigureOutbox(outboxPath);
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
            new CollectorRuntimeOptions { MaxDurableFacts = 1 });
        var instance = runtime.CreateInstance(
            package,
            new SubjectReference(Guid.CreateVersion7(), SubjectKind.Machine),
            new CollectorInstanceSpec(1, 1, config.RootElement.Clone()));
        await using var activation = await runtime.ActivateInProcessAsync(
            instance.CollectorInstanceId,
            package,
            collector);

        clock.Advance(TimeSpan.FromSeconds(30));
        monitor.PushCurrentSnapshot();
        clock.Advance(TimeSpan.FromSeconds(30));
        observations.Activate("win:chrome", "Docs", "Chrome");
        clock.Advance(TimeSpan.FromSeconds(30));

        monitor.PushCurrentSnapshot();

        using var outbox = JsonDocument.Parse(File.ReadAllText(outboxPath));
        var entry = Assert.Single(outbox.RootElement.EnumerateArray());
        Assert.NotEqual(Guid.Empty, entry.GetProperty("MessageId").GetGuid());
        var fact = entry.GetProperty("Fact");
        Assert.Equal(1, fact.GetProperty("SchemaRevision").GetInt32());
        Assert.Equal(1, fact.GetProperty("Revision").GetInt64());
        Assert.False(fact.GetProperty("Time").GetProperty("IsFinal").GetBoolean());
        Assert.Equal(
            "win:chrome",
            fact.GetProperty("Payload").GetProperty("appIdentityKey").GetString());
        Assert.Equal(
            "Docs",
            fact.GetProperty("Payload").GetProperty("title").GetString());
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
