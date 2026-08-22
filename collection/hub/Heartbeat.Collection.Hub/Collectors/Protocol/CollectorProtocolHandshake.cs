using System.Collections.Immutable;

namespace Heartbeat.Collection.Hub.Collectors.Protocol;

internal sealed class CollectorProtocolHandshake
{
    private readonly List<CollectorHandshakeStep> _transcript = [];
    private CollectorHandshakeStep? _lastStep;

    public void AcceptHello() => Advance(null, CollectorHandshakeStep.Hello);

    public void AcceptInitialize() => Advance(
        CollectorHandshakeStep.Hello,
        CollectorHandshakeStep.Initialize);

    public void AcceptStreamsOpen() => Advance(
        CollectorHandshakeStep.Initialize,
        CollectorHandshakeStep.StreamsOpen);

    public void AcceptReady() => Advance(
        CollectorHandshakeStep.StreamsOpen,
        CollectorHandshakeStep.Ready);

    public ImmutableArray<CollectorHandshakeStep> Complete()
    {
        if (_lastStep != CollectorHandshakeStep.Ready)
            throw new InvalidOperationException("Collector Protocol handshake did not reach Ready.");
        return _transcript.ToImmutableArray();
    }

    private void Advance(CollectorHandshakeStep? expected, CollectorHandshakeStep next)
    {
        if (_lastStep != expected)
            throw new InvalidOperationException(
                $"Collector Protocol handshake expected '{expected?.ToString() ?? "start"}' before '{next}'.");
        _lastStep = next;
        _transcript.Add(next);
    }
}
