using System.Collections.Immutable;
using Heartbeat.Collection.Hub.Collectors.Packages;
using Heartbeat.Collection.Hub.Collectors.Runtime;

namespace Heartbeat.Collection.Hub.Collectors.Protocol;

public sealed class InProcessCollectorActivation : IAsyncDisposable
{
    private readonly CollectorRuntime _runtime;
    private readonly IInProcessCollector _collector;
    private readonly CollectorActivationSession _session;
    private readonly object _stopGate = new();
    private Task? _stopTask;

    internal InProcessCollectorActivation(
        CollectorRuntime runtime,
        CollectorActivationSession session,
        IInProcessCollector collector,
        IReadOnlyDictionary<string, InProcessFactStream> streams)
    {
        _runtime = runtime;
        _session = session;
        _collector = collector;
        Streams = streams;
    }

    public Guid ActivationId => _session.ActivationId;
    public Guid HelloMessageId => _session.HelloMessageId;
    public CollectorActivationState State => _session.State;
    public ActivationDeliveryCapability DeliveryCapability => _session.DeliveryCapability;
    public IReadOnlyList<CollectorHandshakeStep> HandshakeTranscript => _session.HandshakeTranscript;
    public IReadOnlyDictionary<string, InProcessFactStream> Streams { get; }
    public InProcessCollectorDrainResult? DrainResult { get; private set; }
    internal LocalCollectorPackage Package => _session.Package;
    internal CollectorActivationSession Session => _session;

    public ValueTask StopAsync(CancellationToken cancellationToken = default)
        => new(WaitForStopAsync(cancellationToken));

    private async Task WaitForStopAsync(CancellationToken cancellationToken)
    {
        Task stopTask;
        lock (_stopGate)
        {
            _stopTask ??= StopCoreAsync();
            stopTask = _stopTask;
        }
        try
        {
            await stopTask.WaitAsync(cancellationToken);
        }
        catch
        {
            if (stopTask.IsCompleted)
            {
                lock (_stopGate)
                {
                    if (ReferenceEquals(_stopTask, stopTask))
                        _stopTask = null;
                }
            }
            throw;
        }
    }

    public ValueTask DisposeAsync() => StopAsync();

    private async Task StopCoreAsync()
    {
        if (!_session.BeginDrain())
            return;
        DrainResult = await _collector.StopAsync(CancellationToken.None);
        _session.CompleteStop(() => _runtime.CompleteStop(this));
    }
}

/// <summary>
/// Binding-neutral view of an accepted <c>streams.open</c> response. Calling
/// <see cref="ReadyAsync"/> emits the Collector's ready transition and returns the live session
/// only after the Hub has granted its writer leases.
/// </summary>
public sealed class InProcessCollectorStreamsOpened
{
    private readonly object _gate = new();
    private readonly Func<CancellationToken, InProcessCollectorActivation> _completeReady;
    private InProcessCollectorActivation? _activation;

    internal InProcessCollectorStreamsOpened(
        Guid activationId,
        IReadOnlyDictionary<string, FactStreamDescriptor> streams,
        Func<CancellationToken, InProcessCollectorActivation> completeReady)
    {
        ActivationId = activationId;
        Streams = streams.ToImmutableDictionary(StringComparer.Ordinal);
        _completeReady = completeReady;
    }

    public Guid ActivationId { get; }
    public IReadOnlyDictionary<string, FactStreamDescriptor> Streams { get; }

    public ValueTask<InProcessCollectorActivation> ReadyAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _activation ??= _completeReady(cancellationToken);
            return ValueTask.FromResult(_activation);
        }
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
