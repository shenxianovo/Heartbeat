using System.Collections.Immutable;
using Heartbeat.Collection.Hub.Collectors.Packages;
using Heartbeat.Collection.Hub.Collectors.Runtime;

namespace Heartbeat.Collection.Hub.Collectors.Protocol;

public sealed class InProcessCollectorActivation : IAsyncDisposable
{
    private readonly CollectorActivationSession _session;
    private readonly CollectorActivationLifetime _lifetime;

    internal InProcessCollectorActivation(
        CollectorActivationSession session,
        CollectorActivationLifetime lifetime,
        IReadOnlyDictionary<string, InProcessFactStream> streams)
    {
        _session = session;
        _lifetime = lifetime;
        Streams = streams;
    }

    public Guid ActivationId => _session.ActivationId;
    public Guid HelloMessageId => _session.HelloMessageId;
    public CollectorActivationState State => _session.State;
    public ActivationDeliveryCapability DeliveryCapability => _session.DeliveryCapability;
    public IReadOnlyList<CollectorHandshakeStep> HandshakeTranscript => _session.HandshakeTranscript;
    public IReadOnlyDictionary<string, InProcessFactStream> Streams { get; }
    public InProcessCollectorDrainResult? DrainResult =>
        _lifetime.Terminal.IsCompletedSuccessfully
            ? _lifetime.Terminal.Result.DrainOutcome.ToInProcess()
            : null;
    internal LocalCollectorPackage Package => _session.Package;
    internal CollectorActivationSession Session => _session;

    internal bool TryCommitAcknowledgement(Action commit) =>
        _session.TryCommitAcknowledgement(commit);

    public async ValueTask StopAsync(CancellationToken cancellationToken = default) =>
        _ = await _lifetime.RequestStopAsync(
            new CollectorActivationStopIntent(CollectorActivationStopCause.Deactivated),
            cancellationToken);

    public ValueTask DisposeAsync() => StopAsync();
}

/// <summary>
/// Binding-neutral view of an accepted <c>streams.open</c> response. Calling
/// <see cref="ReadyAsync"/> emits the Collector's ready transition and returns the live session
/// only after the Hub has granted its writer leases.
/// </summary>
public sealed class InProcessCollectorStreamsOpened
{
    private readonly object _gate = new();
    private readonly Func<ValueTask<InProcessCollectorActivation>> _completeReady;
    private Task<InProcessCollectorActivation>? _ready;

    internal InProcessCollectorStreamsOpened(
        Guid activationId,
        IReadOnlyDictionary<string, FactStreamDescriptor> streams,
        Func<ValueTask<InProcessCollectorActivation>> completeReady)
    {
        ActivationId = activationId;
        Streams = streams.ToImmutableDictionary(StringComparer.Ordinal);
        _completeReady = completeReady;
    }

    public Guid ActivationId { get; }
    public IReadOnlyDictionary<string, FactStreamDescriptor> Streams { get; }

    public async ValueTask<InProcessCollectorActivation> ReadyAsync(
        CancellationToken cancellationToken = default)
    {
        Task<InProcessCollectorActivation> ready;
        lock (_gate)
        {
            _ready ??= _completeReady().AsTask();
            ready = _ready;
        }
        return await ready.WaitAsync(cancellationToken);
    }
}

public sealed class InProcessFactStream
{
    private readonly CollectorActivationSession _session;

    internal InProcessFactStream(
        CollectorActivationSession session,
        FactStreamDescriptor descriptor)
    {
        _session = session;
        Descriptor = descriptor;
    }

    public FactStreamDescriptor Descriptor { get; }

    public ValueTask<FactBatchAcknowledgement> PublishAsync(
        Guid messageId,
        IReadOnlyList<FactSubmission> facts,
        CancellationToken cancellationToken = default) =>
        _session.PublishAsync(Descriptor.StreamId, messageId, facts, cancellationToken);

    public ValueTask<GapDeliveryOutcome> ReportGapAsync(
        Guid messageId,
        StreamGapReport gap,
        CancellationToken cancellationToken = default) =>
        _session.ReportGapAsync(Descriptor.StreamId, messageId, gap, cancellationToken);
}
