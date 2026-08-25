using System.Text;
using System.Text.Json;
using Heartbeat.Collection.Hub.Collectors.Packages;
using Heartbeat.Collection.Hub.Collectors.Protocol;
using Heartbeat.Collection.Hub.Collectors.Runtime;
using Heartbeat.Collection.Hub.Segments;
using Heartbeat.Collection.Hub.Storage;
using Heartbeat.Collection.Hub.Tests.Collectors;
using Heartbeat.Collection.Hub.Time;
using Heartbeat.Collection.Hub.Upload;
using Heartbeat.Collection.Hub.Http;
using Heartbeat.Core;
using Heartbeat.Core.DTOs.Segments;

namespace Heartbeat.Collection.Hub.Tests.Collectors.Protocol;

public class ManagedProcessCollectorProtocolTranscriptTests
{
    private static string ReferencePackagePath => Path.Combine(
        AppContext.BaseDirectory,
        "Fixtures",
        "ReferenceCollectorPackage");

    [Fact]
    public async Task HappyPath_UsesSharedTranscriptAndPublishesAccountSegment()
    {
        using var packageCopy = ManagedReferenceCollectorPackage.Create();
        var package = LocalCollectorPackage.Load(packageCopy.Path);
        using var stateDirectory = TemporaryDirectory.Create();
        var sink = new SegmentIngestService(new TestClock(
            new DateTimeOffset(2026, 8, 22, 12, 10, 0, TimeSpan.Zero)));
        using var runtime = CollectorRuntime.Open(
            Path.Combine(stateDirectory.Path, "collector-runtime.json"),
            sink);
        var accountSubject = new SubjectReference(
            Guid.Parse("0198d5df-5df3-70a1-937d-68a7d64623e2"),
            SubjectKind.Account);
        using var config = JsonDocument.Parse("{}");
        var instance = runtime.CreateInstance(
            package,
            accountSubject,
            new CollectorInstanceSpec(7, 1, config.RootElement.Clone()));

        var activation = await runtime.ActivateManagedProcessAsync(
            instance.CollectorInstanceId,
            package,
            new ManagedProcessActivationOptions
            {
                StartupTimeout = TimeSpan.FromSeconds(10),
                DrainGracePeriod = TimeSpan.FromSeconds(5)
            });

        CollectorProtocolTranscriptContract.AssertHappyPath(
            activation.State,
            activation.DeliveryCapability,
            activation.HandshakeTranscript,
            activation.Streams,
            accountSubject,
            "reference.account");
        var segment = await WaitForSegmentAsync(sink);
        Assert.Equal("reference.account", segment.Source);
        Assert.Equal("reference.account|online", segment.IdentityKey);
        ((IUploadSource<ActivitySegmentItem>)sink).Reinject([segment]);
        List<ActivitySegmentItem>? uploaded = null;
        var upload = new UploadStream<ActivitySegmentItem>(
            "reference account segment",
            sink,
            batch =>
            {
                uploaded = batch;
                return Task.FromResult(ApiResult.Ok);
            },
            new MemoryCache<ActivitySegmentItem>(),
            SnapshotCompaction.KeepLatest);
        await upload.DrainAsync();
        Assert.Equal("reference.account|online", Assert.Single(uploaded!).IdentityKey);

        await activation.StopAsync();

        Assert.Equal(CollectorActivationState.Stopped, activation.State);
        Assert.Equal(CollectorRuntimePhase.Stopped, activation.RuntimeState.Phase);
        Assert.Equal(0, activation.RuntimeState.PendingFacts);
        Assert.Equal(0, activation.RuntimeState.PendingGaps);
        Assert.False(activation.RuntimeState.ProcessTerminated);
    }

    [Fact]
    public async Task Activation_WithoutUniqueManagedProcessArtifact_IsRejectedBeforeLaunch()
    {
        using var packageCopy = ReferenceCollectorPackageCopy.Create(ReferencePackagePath);
        var package = LocalCollectorPackage.Load(packageCopy.Path);
        using var stateDirectory = TemporaryDirectory.Create();
        using var runtime = CollectorRuntime.Open(
            Path.Combine(stateDirectory.Path, "collector-runtime.json"),
            new RecordingSegmentSink());
        using var config = JsonDocument.Parse("{}");
        var instance = runtime.CreateInstance(
            package,
            new SubjectReference(Guid.CreateVersion7(), SubjectKind.Machine),
            new CollectorInstanceSpec(1, 1, config.RootElement.Clone()));

        var error = await Assert.ThrowsAsync<CollectorActivationException>(async () =>
            await runtime.ActivateManagedProcessAsync(
                instance.CollectorInstanceId,
                package));

        Assert.Equal("package_mismatch", error.Error.Code);
        Assert.Contains("exactly one Artifact", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProcessExit_EndsActivationAndReleasesWriterForReplacement()
    {
        using var fixture = ManagedRuntimeFixture.Create();
        var failed = await fixture.Runtime.ActivateManagedProcessAsync(
            fixture.Instance.CollectorInstanceId,
            fixture.Package,
            Options("exit_after_ready"));

        await failed.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForPhaseAsync(failed, CollectorRuntimePhase.Failed);

        Assert.Equal(CollectorActivationState.Stopped, failed.State);
        Assert.Equal("process_exited", failed.RuntimeState.Failure?.Code);
        var replacement = await fixture.Runtime.ActivateManagedProcessAsync(
            fixture.Instance.CollectorInstanceId,
            fixture.Package,
            Options());
        Assert.Equal(failed.Streams["activity"].StreamId, replacement.Streams["activity"].StreamId);
        await replacement.StopAsync();
    }

    [Fact]
    public async Task Hello_OnlySelectsCapabilitiesSharedByCollectorPackageAndHub()
    {
        using var fixture = ManagedRuntimeFixture.Create();

        var activation = await fixture.Runtime.ActivateManagedProcessAsync(
            fixture.Instance.CollectorInstanceId,
            fixture.Package,
            Options("extra_capability"));

        Assert.Equal(CollectorRuntimePhase.Ready, activation.RuntimeState.Phase);
        await activation.StopAsync();
    }

    [Fact]
    public async Task ProtocolCorruption_ProducesStructuredFailureAndStopsProcess()
    {
        using var fixture = ManagedRuntimeFixture.Create();
        var activation = await fixture.Runtime.ActivateManagedProcessAsync(
            fixture.Instance.CollectorInstanceId,
            fixture.Package,
            Options("corrupt_after_ready"));

        await activation.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForPhaseAsync(activation, CollectorRuntimePhase.Failed);

        Assert.Equal(CollectorActivationState.Stopped, activation.State);
        Assert.Equal("protocol_invalid_message", activation.RuntimeState.Failure?.Code);
        Assert.True(activation.RuntimeState.ProcessTerminated);
    }

    [Theory]
    [InlineData("invalid_capability_type")]
    [InlineData("unknown_hello_field")]
    [InlineData("uppercase_uuid")]
    public async Task InvalidHelloFields_AreReportedAsProtocolCorruption(string behavior)
    {
        using var fixture = ManagedRuntimeFixture.Create();

        var error = await Assert.ThrowsAsync<CollectorActivationException>(async () =>
            await fixture.Runtime.ActivateManagedProcessAsync(
                fixture.Instance.CollectorInstanceId,
                fixture.Package,
                Options(behavior)));

        Assert.Equal("protocol_invalid_message", error.Error.Code);
        Assert.Equal(
            "protocol_invalid_message",
            fixture.Runtime.GetManagedProcessRuntimeState(fixture.Instance.CollectorInstanceId).Failure?.Code);
    }

    [Fact]
    public async Task ProcessExitBeforeHello_IsDistinctFromProtocolCorruption()
    {
        using var fixture = ManagedRuntimeFixture.Create();

        var error = await Assert.ThrowsAsync<CollectorActivationException>(async () =>
            await fixture.Runtime.ActivateManagedProcessAsync(
                fixture.Instance.CollectorInstanceId,
                fixture.Package,
                Options("exit_before_hello")));

        Assert.Equal("process_exited", error.Error.Code);
        var state = fixture.Runtime.GetManagedProcessRuntimeState(fixture.Instance.CollectorInstanceId);
        Assert.Equal("process_exited", state.Failure?.Code);
        Assert.Equal(0, state.Failure?.ProcessExitCode);
    }

    [Fact]
    public async Task ProtocolCorruptionDuringDrain_IsFailedAndWriterIsReleased()
    {
        using var fixture = ManagedRuntimeFixture.Create();
        var activation = await fixture.Runtime.ActivateManagedProcessAsync(
            fixture.Instance.CollectorInstanceId,
            fixture.Package,
            Options("corrupt_on_drain"));

        await activation.StopAsync();

        Assert.Equal(CollectorActivationState.Stopped, activation.State);
        Assert.Equal(CollectorRuntimePhase.Failed, activation.RuntimeState.Phase);
        Assert.Equal("protocol_invalid_message", activation.RuntimeState.Failure?.Code);
        var replacement = await fixture.Runtime.ActivateManagedProcessAsync(
            fixture.Instance.CollectorInstanceId,
            fixture.Package,
            Options());
        await replacement.StopAsync();
    }

    [Fact]
    public async Task DrainWriteDisconnect_IsFailedAndWriterIsReleased()
    {
        using var fixture = ManagedRuntimeFixture.Create();
        var activation = await fixture.Runtime.ActivateManagedProcessAsync(
            fixture.Instance.CollectorInstanceId,
            fixture.Package,
            Options(disconnectDrain: true));

        await activation.StopAsync();

        Assert.Equal(CollectorActivationState.Stopped, activation.State);
        Assert.Equal(CollectorRuntimePhase.Failed, activation.RuntimeState.Phase);
        Assert.Equal("protocol_invalid_message", activation.RuntimeState.Failure?.Code);
        var replacement = await fixture.Runtime.ActivateManagedProcessAsync(
            fixture.Instance.CollectorInstanceId,
            fixture.Package,
            Options());
        await replacement.StopAsync();
    }

    [Fact]
    public async Task StartupTimeout_ProducesStructuredFailure()
    {
        using var fixture = ManagedRuntimeFixture.Create();

        var error = await Assert.ThrowsAsync<CollectorActivationException>(async () =>
            await fixture.Runtime.ActivateManagedProcessAsync(
                fixture.Instance.CollectorInstanceId,
                fixture.Package,
                new ManagedProcessActivationOptions
                {
                    StartupTimeout = TimeSpan.FromMilliseconds(250),
                    DrainGracePeriod = TimeSpan.FromSeconds(1),
                    EnvironmentVariables = new Dictionary<string, string>
                    {
                        ["HEARTBEAT_REFERENCE_BEHAVIOR"] = "startup_timeout"
                    }
                }));

        Assert.Equal("activation_start_timeout", error.Error.Code);
        var state = fixture.Runtime.GetManagedProcessRuntimeState(fixture.Instance.CollectorInstanceId);
        Assert.Equal(CollectorRuntimePhase.Failed, state.Phase);
        Assert.Equal("activation_start_timeout", state.Failure?.Code);
    }

    [Fact]
    public async Task DrainDeadline_TerminatesUnresponsiveProcessAndKeepsPendingCountsUnknown()
    {
        using var fixture = ManagedRuntimeFixture.Create();
        var activation = await fixture.Runtime.ActivateManagedProcessAsync(
            fixture.Instance.CollectorInstanceId,
            fixture.Package,
            new ManagedProcessActivationOptions
            {
                StartupTimeout = TimeSpan.FromSeconds(5),
                DrainGracePeriod = TimeSpan.FromMilliseconds(250),
                EnvironmentVariables = new Dictionary<string, string>
                {
                    ["HEARTBEAT_REFERENCE_BEHAVIOR"] = "ignore_drain"
                }
            });

        await activation.StopAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(CollectorRuntimePhase.Stopped, activation.RuntimeState.Phase);
        Assert.True(activation.RuntimeState.ProcessTerminated);
        Assert.Null(activation.RuntimeState.PendingFacts);
        Assert.Null(activation.RuntimeState.PendingGaps);
    }

    private static async Task<Heartbeat.Core.DTOs.Segments.ActivitySegmentItem> WaitForSegmentAsync(
        SegmentIngestService sink)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var segments = sink.GetAndClearSegments();
            if (segments.Count != 0)
                return Assert.Single(segments);
            await Task.Delay(20, timeout.Token);
        }
    }

    private static ManagedProcessActivationOptions Options(
        string? behavior = null,
        bool disconnectDrain = false) => new()
        {
            StartupTimeout = TimeSpan.FromSeconds(5),
            DrainGracePeriod = TimeSpan.FromSeconds(2),
            StandardInputDecorator = disconnectDrain
            ? writer => new DisconnectOnDrainWriter(writer)
            : null,
            EnvironmentVariables = behavior is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string> { ["HEARTBEAT_REFERENCE_BEHAVIOR"] = behavior }
        };

    private static async Task WaitForPhaseAsync(
        ManagedProcessCollectorActivation activation,
        CollectorRuntimePhase phase)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (activation.RuntimeState.Phase != phase)
            await Task.Delay(20, timeout.Token);
    }

    private sealed class RecordingSegmentSink : ISegmentSink, IDurableSegmentProjectionSink
    {
        public void Push(List<Heartbeat.Core.DTOs.Segments.ActivitySegmentItem> snapshots) { }
        public void UpsertDurable(Heartbeat.Core.DTOs.Segments.ActivitySegmentItem snapshot, long revision) { }
        public void ReplayDurable(Heartbeat.Core.DTOs.Segments.ActivitySegmentItem snapshot, long revision) { }
        public void RetractDurable(Guid segmentId, long revision) { }
    }

    private sealed class DisconnectOnDrainWriter(TextWriter inner) : TextWriter
    {
        public override Encoding Encoding => inner.Encoding;

        public override Task WriteLineAsync(
            ReadOnlyMemory<char> buffer,
            CancellationToken cancellationToken = default) =>
            buffer.Span.Contains("\"type\":\"activation.drain\"", StringComparison.Ordinal)
                ? Task.FromException(new IOException("Simulated disconnected ManagedProcess stdin."))
                : inner.WriteLineAsync(buffer, cancellationToken);

        public override Task FlushAsync() => inner.FlushAsync();
        public override Task FlushAsync(CancellationToken cancellationToken) =>
            inner.FlushAsync(cancellationToken);
    }

    private sealed class TestClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow => utcNow;
    }

    private sealed class MemoryCache<T> : ICache<T>
    {
        private List<T> _items = [];
        public CacheFileStatus Status => CacheFileStatus.Ready;
        public void Add(List<T> items) => _items.AddRange(items);
        public List<T> Load() => [.. _items];
        public void Replace(List<T> items) => _items = [.. items];
        public void Clear() => _items.Clear();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path) => Path = path;

        public string Path { get; }

        public static TemporaryDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"heartbeat-managed-process-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new TemporaryDirectory(path);
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private sealed class ManagedRuntimeFixture : IDisposable
    {
        private readonly ManagedReferenceCollectorPackage _packageCopy;
        private readonly TemporaryDirectory _stateDirectory;

        private ManagedRuntimeFixture(
            ManagedReferenceCollectorPackage packageCopy,
            TemporaryDirectory stateDirectory,
            LocalCollectorPackage package,
            CollectorRuntime runtime,
            CollectorInstance instance)
        {
            _packageCopy = packageCopy;
            _stateDirectory = stateDirectory;
            Package = package;
            Runtime = runtime;
            Instance = instance;
        }

        public LocalCollectorPackage Package { get; }
        public CollectorRuntime Runtime { get; }
        public CollectorInstance Instance { get; }

        public static ManagedRuntimeFixture Create()
        {
            var packageCopy = ManagedReferenceCollectorPackage.Create();
            var stateDirectory = TemporaryDirectory.Create();
            try
            {
                var package = LocalCollectorPackage.Load(packageCopy.Path);
                var runtime = CollectorRuntime.Open(
                    Path.Combine(stateDirectory.Path, "collector-runtime.json"),
                    new RecordingSegmentSink());
                using var config = JsonDocument.Parse("{}");
                var instance = runtime.CreateInstance(
                    package,
                    new SubjectReference(Guid.CreateVersion7(), SubjectKind.Account),
                    new CollectorInstanceSpec(1, 1, config.RootElement.Clone()));
                return new ManagedRuntimeFixture(packageCopy, stateDirectory, package, runtime, instance);
            }
            catch
            {
                stateDirectory.Dispose();
                packageCopy.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            Runtime.Dispose();
            _stateDirectory.Dispose();
            _packageCopy.Dispose();
        }
    }
}
