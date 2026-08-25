using Heartbeat.Collection.Hub.Collectors.Protocol;
using Heartbeat.Collection.Hub.Collectors.Runtime;

namespace Heartbeat.Collection.Hub.Tests.Collectors.Protocol;

internal static class CollectorProtocolTranscriptContract
{
    public static void AssertHappyPath(
        CollectorActivationState state,
        ActivationDeliveryCapability deliveryCapability,
        IReadOnlyList<CollectorHandshakeStep> handshakeTranscript,
        IReadOnlyDictionary<string, FactStreamDescriptor> streams,
        SubjectReference expectedSubject,
        string expectedSource)
    {
        Assert.Equal(CollectorActivationState.Ready, state);
        Assert.Equal(ActivationDeliveryCapability.Complete, deliveryCapability);
        Assert.Equal(
            [
                CollectorHandshakeStep.Hello,
                CollectorHandshakeStep.Initialize,
                CollectorHandshakeStep.StreamsOpen,
                CollectorHandshakeStep.Ready
            ],
            handshakeTranscript);
        var stream = Assert.Single(streams);
        Assert.Equal("activity", stream.Key);
        Assert.Equal(expectedSubject, stream.Value.Subject);
        Assert.Equal(expectedSource, stream.Value.Source);
    }
}
