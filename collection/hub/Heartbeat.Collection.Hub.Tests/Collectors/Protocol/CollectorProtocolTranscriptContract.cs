using Heartbeat.Collection.Hub.Collectors.Protocol;

namespace Heartbeat.Collection.Hub.Tests.Collectors.Protocol;

internal static class CollectorProtocolTranscriptContract
{
    public static void AssertReady(
        CollectorActivationState state,
        ActivationDeliveryCapability deliveryCapability,
        IReadOnlyList<CollectorHandshakeStep> handshakeTranscript)
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
    }
}
