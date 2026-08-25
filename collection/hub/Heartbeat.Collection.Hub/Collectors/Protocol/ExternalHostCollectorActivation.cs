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
    internal ExternalHostCollectorActivation(
        CollectorRuntime runtime,
        Guid activationId,
        Guid helloMessageId,
        LocalCollectorPackage package,
        IReadOnlyDictionary<string, FactStreamDescriptor> streams)
    {
        Runtime = runtime;
        ActivationId = activationId;
        HelloMessageId = helloMessageId;
        Package = package;
        Streams = streams.ToImmutableDictionary(StringComparer.Ordinal);
        State = CollectorActivationState.OpeningStreams;
    }

    public Guid ActivationId { get; }
    public Guid HelloMessageId { get; }
    public CollectorActivationState State { get; internal set; }
    public ExternalHostActivationStopReason? StopReason { get; internal set; }
    public bool ExternalHostWasTerminated => false;
    public IReadOnlyDictionary<string, FactStreamDescriptor> Streams { get; }
    internal CollectorRuntime Runtime { get; }
    internal LocalCollectorPackage Package { get; }

    public ValueTask<FactBatchAcknowledgement> PublishAsync(
        Guid streamId,
        Guid messageId,
        IReadOnlyList<FactSubmission> facts,
        CancellationToken cancellationToken = default) =>
        Runtime.PublishAsync(ActivationId, streamId, messageId, facts, cancellationToken);

    public ValueTask<GapDeliveryOutcome> ReportGapAsync(
        Guid streamId,
        Guid messageId,
        StreamGapReport gap,
        CancellationToken cancellationToken = default) =>
        Runtime.ReportGapAsync(ActivationId, streamId, messageId, gap, cancellationToken);
}
