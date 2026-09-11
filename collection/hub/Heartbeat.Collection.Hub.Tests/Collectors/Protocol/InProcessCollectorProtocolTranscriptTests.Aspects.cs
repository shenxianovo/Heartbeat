using System.Text.Json;
using System.Text.Json.Nodes;
using Heartbeat.Collection.Hub.Collectors.Protocol;
using Heartbeat.Collection.Hub.Collectors.Runtime;
using Heartbeat.Core.DTOs.Facts;
using Heartbeat.Core.Facts;

namespace Heartbeat.Collection.Hub.Tests.Collectors.Protocol;

public partial class InProcessCollectorProtocolTranscriptTests
{
    [Fact]
    public async Task AspectSurvivesRestart_AndIsPartOfExactUploadAcknowledgement()
    {
        await using var fixture = await ActivatedRuntimeFixture.CreateAsync(new CollectorRuntimeOptions { });
        var stream = fixture.Activation.Streams["activity"];
        var json = JsonSerializer.SerializeToNode(CreateFact(stream.Descriptor.StreamId), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        json["aspect"] = "custom.snapshot";
        var machine = new ObservationObjectReference("machine", ObservationObjectScopes.Machine, "machine-a");
        var app = new ObservationObjectReference("app", ObservationObjectScopes.AppIdentity, "mac:com.google.chrome");
        var fact = json.Deserialize<FactSubmission>(new JsonSerializerOptions(JsonSerializerDefaults.Web))! with
        { CollectorId = Guid.NewGuid(), Foi = machine, Relations = [ObservationCompatibility.ObservedOn(machine, app)] };
        Assert.Equal(FactDeliveryStatus.Committed, Assert.Single((await stream.PublishAsync(Guid.CreateVersion7(), [fact])).Results).Status);
        await fixture.Activation.DisposeAsync();
        fixture.Runtime.Dispose();
        using var restarted = CollectorRuntime.Open(fixture.StatePath, fixture.Sink, new CollectorRuntimeOptions { });
        var item = Assert.Single(restarted.ReadPendingFacts());
        Assert.Equal("custom.snapshot", item.Fact!.Aspect);
        item.Fact.Aspect = "wrong.meaning";
        restarted.ConfirmUploadedFacts([item]);
        Assert.Single(restarted.ReadPendingFacts());
        item = Assert.Single(restarted.ReadPendingFacts());
        item.Fact!.Relations![0].Members[0] = new("device", machine with { Key = "wrong-machine" });
        restarted.ConfirmUploadedFacts([item]);
        Assert.Single(restarted.ReadPendingFacts());
        restarted.ConfirmUploadedFacts(restarted.ReadPendingFacts());
        Assert.Empty(restarted.ReadPendingFacts());
    }
    [Fact]
    public async Task VersionSevenCachePreservesKnownAndUnknownObjectsAndDelivery()
    {
        await using var fixture = await ActivatedRuntimeFixture.CreateAsync(new CollectorRuntimeOptions { });
        var stream = fixture.Activation.Streams["activity"];
        var machine = new ObservationObjectReference("machine", ObservationObjectScopes.Machine, "machine-a");
        var app = new ObservationObjectReference("app", ObservationObjectScopes.AppIdentity, "win:chrome");
        var known = CreateFact(stream.Descriptor.StreamId) with { CollectorId = Guid.NewGuid(), Foi = machine,
            Aspect = "desktop-activity", Relations = [ObservationCompatibility.ObservedOn(machine, app)],
            Payload = JsonSerializer.SerializeToElement(new { activityKey = "chrome", appIdentityKey = app.Key }) };
        var unknown = known with { FactId = Guid.CreateVersion7(), CollectorId = null, Foi = null, Relations = [] };
        Assert.All((await stream.PublishAsync(Guid.CreateVersion7(), [known, unknown])).Results,
            outcome => Assert.Equal(FactDeliveryStatus.Committed, outcome.Status));
        fixture.Runtime.ConfirmUploadedFacts(fixture.Runtime.ReadPendingFacts().Where(f => f.Fact!.FactId == known.FactId).ToArray());
        await fixture.Activation.DisposeAsync();
        fixture.Runtime.Dispose();
        var previous = JsonNode.Parse(File.ReadAllText(fixture.StatePath))!;
        previous["schemaVersion"] = 7;
        foreach (var node in previous["facts"]!.AsArray().OfType<JsonObject>())
        {
            var isKnown = node["factId"]!.GetValue<Guid>() == known.FactId;
            node["observerId"] = isKnown ? JsonValue.Create(known.CollectorId) : null;
            node["target"] = isKnown ? JsonSerializer.SerializeToNode(new FactTarget("device", machine.Key), new JsonSerializerOptions(JsonSerializerDefaults.Web)) : null;
            node.Remove("collectorId"); node.Remove("foi"); node.Remove("relations");
        }
        var oldBytes = previous.ToJsonString();
        File.WriteAllText(fixture.StatePath, oldBytes);
        using var restarted = CollectorRuntime.Open(fixture.StatePath, fixture.Sink, new CollectorRuntimeOptions { });
        Assert.Equal(oldBytes, File.ReadAllText(fixture.StatePath + ".v7.bak"));
        var pending = Assert.Single(restarted.ReadPendingFacts()).Fact!;
        Assert.Equal(unknown.FactId, pending.FactId);
        Assert.Null(pending.CollectorId); Assert.Null(pending.Foi); Assert.Empty(pending.Relations!);
        var current = JsonNode.Parse(File.ReadAllText(fixture.StatePath))!;
        var saved = current["facts"]!.AsArray().Single(f => f!["factId"]!.GetValue<Guid>() == known.FactId)!;
        Assert.True(saved["delivered"]!.GetValue<bool>());
        Assert.Equal(known.CollectorId, saved["collectorId"]!.GetValue<Guid>());
        Assert.Equal(machine.Key, saved["foi"]!["key"]!.GetValue<string>());
        Assert.Equal("observed-on", saved["relations"]![0]!["kind"]!.GetValue<string>());
        Assert.Equal(previous["facts"]![0]!["payload"]!.ToJsonString(), saved["payload"]!.ToJsonString());
    }
}
