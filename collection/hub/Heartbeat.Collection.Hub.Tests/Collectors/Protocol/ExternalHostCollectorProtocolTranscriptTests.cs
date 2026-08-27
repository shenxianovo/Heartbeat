using System.Text.Json;
using Heartbeat.Collection.Hub.Collectors.Packages;
using Heartbeat.Collection.Hub.Collectors.Protocol;
using Heartbeat.Collection.Hub.Collectors.Runtime;
using Heartbeat.Collection.Hub.Segments;
using Heartbeat.Collection.Hub.Tests.Collectors;
using Heartbeat.Collection.Hub.Time;

namespace Heartbeat.Collection.Hub.Tests.Collectors.Protocol;

public sealed class ExternalHostCollectorProtocolTranscriptTests
{
    private static string ReferencePackagePath => Path.Combine(
        AppContext.BaseDirectory,
        "Fixtures",
        "ReferenceCollectorPackage");

    [Fact]
    public async Task LeaseSession_AppliesFullSpecOpensStreamAndOnlyAckedFactIsDurable()
    {
        using var directory = TemporaryDirectory.Create();
        using var packageCopy = ExternalHostPackageCopy();
        var package = LocalCollectorPackage.Load(packageCopy.Path);
        var sink = new SegmentIngestService(new FixedClock());
        using var runtime = CollectorRuntime.Open(
            Path.Combine(directory.Path, "runtime.json"),
            sink);
        using var config = JsonDocument.Parse("""{"enabled":true,"flushPeriodMs":30000}""");
        var instance = runtime.CreateInstance(
            package,
            new SubjectReference(Guid.CreateVersion7(), SubjectKind.Machine),
            new CollectorInstanceSpec(4, 1, config.RootElement.Clone()));
        var activationId = Guid.CreateVersion7();
        var initialization = runtime.BeginExternalHostActivation(
            instance.CollectorInstanceId,
            package,
            "reference.inprocess",
            package.Artifacts.Single().ContentHash,
            Support(),
            activationId,
            Guid.CreateVersion7());

        Assert.Equal(4, initialization.Spec.SpecRevision);
        Assert.True(initialization.Spec.Config.GetProperty("enabled").GetBoolean());

        var activation = runtime.OpenExternalHostStreams(
            activationId,
            initialization.Spec.SpecRevision,
            [new OutputBinding("activity", "activity", new Dictionary<string, string>())]);
        Assert.Equal(CollectorActivationState.OpeningStreams, activation.State);
        runtime.MarkExternalHostReady(activation, initialization.Spec.SpecRevision);
        CollectorProtocolTranscriptContract.AssertHappyPath(
            activation.State,
            activation.DeliveryCapability,
            activation.HandshakeTranscript,
            activation.Streams,
            instance.Subject,
            "reference");
        var stream = activation.Streams["activity"];
        using var payload = JsonDocument.Parse("""{"identityKey":"reference|work","title":"Work"}""");
        var fact = new FactSubmission(
            stream.StreamId,
            1,
            Guid.CreateVersion7(),
            1,
            null,
            FactRecordState.Present,
            new SegmentFactTime(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow, false),
            payload.RootElement.Clone());

        var acknowledgement = await activation.PublishAsync(
            stream.StreamId,
            Guid.CreateVersion7(),
            [fact]);

        var result = Assert.Single(acknowledgement.Results);
        Assert.Null(result.Error);
        Assert.Equal(FactDeliveryStatus.Committed, result.Status);
        Assert.Single(sink.GetAndClearSegments());
    }

    [Fact]
    public async Task LeaseExpiry_LeavesReadyReleasesWriterAndDoesNotClaimBrowserTermination()
    {
        using var directory = TemporaryDirectory.Create();
        using var packageCopy = ExternalHostPackageCopy();
        var package = LocalCollectorPackage.Load(packageCopy.Path);
        using var runtime = CollectorRuntime.Open(
            Path.Combine(directory.Path, "runtime.json"),
            new SegmentIngestService(new FixedClock()));
        using var config = JsonDocument.Parse("{}");
        var instance = runtime.CreateInstance(
            package,
            new SubjectReference(Guid.CreateVersion7(), SubjectKind.Machine),
            new CollectorInstanceSpec(1, 1, config.RootElement.Clone()));
        var activationId = Guid.CreateVersion7();
        var initialization = runtime.BeginExternalHostActivation(
            instance.CollectorInstanceId,
            package,
            "reference.inprocess",
            package.Artifacts.Single().ContentHash,
            Support(),
            activationId,
            Guid.CreateVersion7());
        var activation = runtime.ReadyExternalHostActivation(
            activationId,
            initialization.Spec.SpecRevision,
            [new OutputBinding("activity", "activity", new Dictionary<string, string>())]);
        var stream = activation.Streams["activity"];

        runtime.StopExternalHostActivation(activation, ExternalHostActivationStopReason.LeaseExpired);

        Assert.Equal(CollectorActivationState.Stopped, activation.State);
        Assert.Equal(ExternalHostActivationStopReason.LeaseExpired, activation.StopReason);
        Assert.False(activation.ExternalHostWasTerminated);
        using var payload = JsonDocument.Parse("""{"identityKey":"late","title":"Late"}""");
        var late = await activation.PublishAsync(
            stream.StreamId,
            Guid.CreateVersion7(),
            [new FactSubmission(
                stream.StreamId,
                1,
                Guid.CreateVersion7(),
                1,
                null,
                FactRecordState.Present,
                new SegmentFactTime(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow, false),
                payload.RootElement.Clone())]);
        Assert.Equal(FactDeliveryStatus.Rejected, Assert.Single(late.Results).Status);
        Assert.Equal("stream_writer_conflict", Assert.Single(late.Results).Error!.Code);
    }

    [Fact]
    public void Hello_SameAttemptAfterRuntimeRestart_IsRejectedInsteadOfAllocatingNewActivation()
    {
        using var directory = TemporaryDirectory.Create();
        using var packageCopy = ExternalHostPackageCopy();
        var package = LocalCollectorPackage.Load(packageCopy.Path);
        var statePath = Path.Combine(directory.Path, "runtime.json");
        using var config = JsonDocument.Parse("{}");
        Guid collectorInstanceId;
        var helloMessageId = Guid.CreateVersion7();
        var firstActivationId = Guid.CreateVersion7();
        using (var runtime = CollectorRuntime.Open(statePath, new SegmentIngestService(new FixedClock())))
        {
            var instance = runtime.CreateInstance(
                package,
                new SubjectReference(Guid.CreateVersion7(), SubjectKind.Machine),
                new CollectorInstanceSpec(1, 1, config.RootElement.Clone()));
            collectorInstanceId = instance.CollectorInstanceId;
            runtime.BeginExternalHostActivation(
                collectorInstanceId,
                package,
                "reference.inprocess",
                package.Artifacts.Single().ContentHash,
                Support(),
                firstActivationId,
                helloMessageId);
        }

        using var reopened = CollectorRuntime.Open(statePath, new SegmentIngestService(new FixedClock()));
        var error = Assert.Throws<CollectorActivationException>(() =>
            reopened.BeginExternalHostActivation(
                collectorInstanceId,
                package,
                "reference.inprocess",
                package.Artifacts.Single().ContentHash,
                Support(),
                Guid.CreateVersion7(),
                helloMessageId));

        Assert.Equal("protocol_invalid_message", error.Error.Code);
        Assert.Contains("previous Runtime session", error.Message, StringComparison.Ordinal);
    }

    private static ProtocolSupport Support() => new(
        [1],
        new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal)
        {
            ["facts.segment"] = [1],
            ["diagnostics.stream-gap"] = [1]
        });

    private static ReferenceCollectorPackageCopy ExternalHostPackageCopy()
    {
        var copy = ReferenceCollectorPackageCopy.Create(ReferencePackagePath);
        var manifest = copy.ReadManifest();
        manifest["artifacts"]![0]!["selector"]!["driver"] = "externalHost";
        copy.WriteManifest(manifest);
        return copy;
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path) => Path = path;

        public string Path { get; }

        public static TemporaryDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"heartbeat-external-host-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new TemporaryDirectory(path);
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
