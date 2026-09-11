using System.Text.Json;
using System.Text.Json.Nodes;
using Heartbeat.Collection.Hub.Collectors.Packages;
using Heartbeat.Collection.Hub.Collectors.Protocol;
using Heartbeat.Collection.Hub.Collectors.Runtime;
using Heartbeat.Collection.Hub.Segments;
using Heartbeat.Collection.Hub.Time;

namespace Heartbeat.Collection.Hub.Tests.Collectors.Protocol;

public partial class InProcessCollectorProtocolTranscriptTests
{
    [Fact]
    public async Task NativeCollectorInitializesWithoutLegacySubjectOrStreamsAndSurvivesRestart()
    {
        using var packageCopy = ReferenceCollectorPackageCopy.Create(ReferencePackagePath);
        var manifestPath = Path.Combine(packageCopy.Path, "collector-manifest.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!;
        manifest["outputs"] = new JsonArray();
        manifest.AsObject().Remove("observationDeclaration");
        manifest.AsObject().Remove("defaultInstance");
        manifest["supportedCapabilities"]!["facts.observation"] = new JsonArray(1, 2);
        File.WriteAllText(manifestPath, manifest.ToJsonString());
        var package = LocalCollectorPackage.Load(packageCopy.Path);
        using var directory = TemporaryDirectory.Create();
        var statePath = Path.Combine(directory.Path, "runtime.json");
        var sink = new SegmentIngestService(new FixedClock(DateTimeOffset.UtcNow));
        Guid instanceId;
        using (var runtime = CollectorRuntime.Open(statePath, sink))
        {
            var instance = runtime.CreateInstance(package,
                new CollectorInstanceSpec(1, 1, JsonSerializer.SerializeToElement(new { })));
            instanceId = instance.CollectorInstanceId;
            var collector = new ReferenceInProcessCollector(bindings: [], protocolSupport:
                new ProtocolSupport([1], new Dictionary<string, IReadOnlyList<int>>
                {
                    ["facts.observation"] = [2], ["facts.segment"] = [1], ["diagnostics.stream-gap"] = [1]
                }));
            await using var activation = await runtime.ActivateInProcessAsync(instanceId, package, collector);
            Assert.Empty(activation.Streams);
            Assert.Equal(Guid.Empty, collector.Initialization!.Instance.Subject.SubjectId);
            var fact = new FactSubmission(Guid.Empty, Guid.CreateVersion7(), 1, null,
                new EventFactTime(DateTimeOffset.Parse("2026-08-22T12:00:00Z")),
                JsonSerializer.SerializeToElement(new { value = "independent" }), instanceId,
                new Heartbeat.Core.DTOs.Facts.ObservationObjectReference("account", "reference", "test-account"),
                "reference.event", [], "event");
            var messageId = Guid.CreateVersion7();
            var ack = await activation.PublishAsync(messageId, [fact]);
            Assert.Equal(FactDeliveryStatus.Committed, Assert.Single(ack.Results).Status);
            var conflicting = await activation.PublishAsync(messageId, [fact with { Source = "different-source" }]);
            Assert.True(conflicting.IsMessageRejected);
            var pending = Assert.Single(runtime.ReadPendingFacts());
            Assert.Null(pending.Stream);
            Assert.Equal(instanceId, pending.Observation!.CollectorId);
        }
        using var restarted = CollectorRuntime.Open(statePath, sink);
        Assert.Equal(Guid.Empty, restarted.GetInstance(instanceId).Subject.SubjectId);
        Assert.Equal("independent", Assert.Single(restarted.ReadPendingFacts()).Observation!.Result!.Value.GetProperty("value").GetString());
    }
}
