using System.Text.Json;
using System.Text.Json.Nodes;
using Heartbeat.Collection.Hub.Collectors.Protocol;
using Heartbeat.Collection.Hub.Collectors.Runtime;

namespace Heartbeat.Collection.Hub.Tests.Collectors.Protocol;

public partial class InProcessCollectorProtocolTranscriptTests
{
    [Fact]
    public async Task AspectSurvivesRestart_AndIsPartOfExactUploadAcknowledgement()
    {
        await using var fixture = await ActivatedRuntimeFixture.CreateAsync(new CollectorRuntimeOptions { EnableFactUpload = true });
        var stream = fixture.Activation.Streams["activity"];
        var json = JsonSerializer.SerializeToNode(CreateFact(stream.Descriptor.StreamId), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        json["aspect"] = "custom.snapshot";
        var fact = json.Deserialize<FactSubmission>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(FactDeliveryStatus.Committed, Assert.Single((await stream.PublishAsync(Guid.CreateVersion7(), [fact])).Results).Status);
        await fixture.Activation.DisposeAsync();
        fixture.Runtime.Dispose();
        using var restarted = CollectorRuntime.Open(fixture.StatePath, fixture.Sink, new CollectorRuntimeOptions { EnableFactUpload = true });
        var item = Assert.Single(restarted.ReadPendingFacts());
        Assert.Equal("custom.snapshot", item.Fact!.Aspect);
        item.Fact.Aspect = "wrong.meaning";
        restarted.ConfirmUploadedFacts([item]);
        Assert.Single(restarted.ReadPendingFacts());
        restarted.ConfirmUploadedFacts(restarted.ReadPendingFacts());
        Assert.Empty(restarted.ReadPendingFacts());
    }
}
