using System.Collections.Immutable;
using Heartbeat.Collection.Hub.Collectors.Packages;
using Heartbeat.Collection.Hub.Collectors.Runtime;

namespace Heartbeat.Collection.Hub.Collectors.Protocol;

/// <summary>
/// A Runtime-owned protocol session for code executed by an external host. Stopping this object
/// only releases protocol resources; it never claims that the browser or its extension process
/// was terminated.
/// </summary>
public sealed class ExternalHostCollectorActivation
{
    private readonly CollectorActivationSession _session;

    internal ExternalHostCollectorActivation(
        CollectorActivationSession session)
    {
        _session = session;
    }

    public Guid ActivationId => _session.ActivationId;
    public Guid HelloMessageId => _session.HelloMessageId;
    public CollectorActivationState State => _session.State;
    public ExternalHostActivationStopReason? StopReason => _session.StopReason;
    public ActivationDeliveryCapability DeliveryCapability => _session.DeliveryCapability;
    public IReadOnlyList<CollectorHandshakeStep> HandshakeTranscript => _session.HandshakeTranscript;
    public bool ExternalHostWasTerminated => false;
    public IReadOnlyDictionary<string, FactStreamDescriptor> Streams => _session.Streams;
    public InProcessCollectorDrainResult? DrainResult { get; private set; }
    internal LocalCollectorPackage Package => _session.Package;
    internal CollectorActivationSession Session => _session;

    internal void CompleteDrain(InProcessCollectorDrainResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        DrainResult ??= result;
    }

    public ValueTask<FactBatchAcknowledgement> PublishAsync(
        Guid streamId,
        Guid messageId,
        IReadOnlyList<FactSubmission> facts,
        CancellationToken cancellationToken = default) =>
        _session.PublishAsync(streamId, messageId, facts, cancellationToken);

    public ValueTask<GapDeliveryOutcome> ReportGapAsync(
        Guid streamId,
        Guid messageId,
        StreamGapReport gap,
        CancellationToken cancellationToken = default) =>
        _session.ReportGapAsync(streamId, messageId, gap, cancellationToken);
}
