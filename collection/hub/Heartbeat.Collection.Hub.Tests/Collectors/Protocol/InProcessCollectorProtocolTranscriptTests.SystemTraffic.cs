using System.Text.Json;
using System.Text.Json.Nodes;
using Heartbeat.Collection.Hub.Collectors.Packages;
using Heartbeat.Collection.Hub.Collectors.Protocol;
using Heartbeat.Collection.Hub.Collectors.Runtime;
using Heartbeat.Collection.Hub.Segments;
using Heartbeat.Core.DTOs.Facts;

namespace Heartbeat.Collection.Hub.Tests.Collectors.Protocol;

public partial class InProcessCollectorProtocolTranscriptTests
{
    [Fact]
    public async Task NativeSourceTrafficUsesOnlyAcknowledgedSourcesAndDoesNotStampDiskReplay()
    {
        using var packageCopy = ReferenceCollectorPackageCopy.Create(ReferencePackagePath);
        var manifestPath = Path.Combine(packageCopy.Path, "collector-manifest.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!;
        manifest["supportedCapabilities"]!["facts.observation"] = new JsonArray(2);
        File.WriteAllText(manifestPath, manifest.ToJsonString());
        var package = LocalCollectorPackage.Load(packageCopy.Path);
        using var directory = TemporaryDirectory.Create();
        var statePath = Path.Combine(directory.Path, "runtime.json");
        var clock = new MutableClock(DateTimeOffset.Parse("2026-08-22T12:00:00Z"));
        var sink = new SegmentIngestService(clock);
        using (var runtime = CollectorRuntime.Open(statePath, sink))
        {
            var instance = runtime.CreateInstance(package,
                new SubjectReference(Guid.NewGuid(), SubjectKind.Machine),
                new CollectorInstanceSpec(1, 1, JsonSerializer.SerializeToElement(new { })));
            var collector = new ReferenceInProcessCollector(protocolSupport:
                new ProtocolSupport([1], new Dictionary<string, IReadOnlyList<int>>
                {
                    ["facts.observation"] = [2], ["facts.segment"] = [1], ["diagnostics.stream-gap"] = [1]
                }));
            await using var activation = await runtime.ActivateInProcessAsync(instance.CollectorInstanceId, package, collector);
            var fact = new FactSubmission(Guid.Empty, Guid.NewGuid(), 1, null,
                new EventFactTime(clock.UtcNow), JsonSerializer.SerializeToElement(new { raw = "result" }),
                instance.CollectorInstanceId, new ObservationObjectReference("account", "fixture", "account"),
                "fixture-event", [], "event", "system");
            var batch = new[] { fact, fact with { FactId = Guid.NewGuid(), Source = "rejected", Foi = null },
                fact with { FactId = Guid.NewGuid(), Source = null } };
            var messageId = Guid.CreateVersion7();
            var ack = await activation.PublishAsync(messageId, batch);
            Assert.False(ack.IsMessageRejected, ack.MessageError?.Message);
            Assert.Equal([true, false, true], ack.Results.Select(result => result.IsAcknowledged));
            Assert.Equal(clock.UtcNow, Assert.Single(sink.SourceLastSeen).Value);
            Assert.True(sink.SourceLastSeen.ContainsKey("system"));
            clock.UtcNow = clock.UtcNow.AddMinutes(1);
            await activation.PublishAsync(messageId, batch);
            Assert.Equal(clock.UtcNow, Assert.Single(sink.SourceLastSeen).Value);
            runtime.ConfirmUploadedFacts(runtime.ReadPendingFacts());
        }
        var restartedSink = new SegmentIngestService(clock);
        using var restarted = CollectorRuntime.Open(statePath, restartedSink);
        Assert.Empty(restartedSink.SourceLastSeen);
    }
}
