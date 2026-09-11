using Heartbeat.Collection.Hub.Collectors.Protocol;
using Heartbeat.Collection.Hub.Collectors.Packages;
using Heartbeat.Core.Facts;

namespace Heartbeat.Collection.Hub.Collectors.Runtime;

public sealed partial class CollectorRuntime
{
    /// <summary>
    /// Recover an exact legacy delivery key for its current ExternalHost writer. Delivered ongoing
    /// Segments remain authoritative evidence when the producer has already removed its ACKed outbox.
    /// This read never reopens a final Fact or changes journal/ACK state, and cannot recover native
    /// streamless observations whose owner cannot be proven by a legacy Stream writer lease.
    /// </summary>
    internal ExternalHostFactRecovery RecoverExternalHostFacts(
        Guid activationId, Guid streamId, IReadOnlyList<Guid>? factIds)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_externalHostActivations.TryGetValue(activationId, out var activation) ||
                activation.State != CollectorActivationState.Ready || activation.Lifetime.HasStopIntent)
                throw ActivationError("activation_stopping", "ExternalHost Activation is not ready for recovery.");
            if (streamId == Guid.Empty ||
                !activation.Streams.Values.Any(stream => stream.StreamId == streamId) ||
                !_streamWriters.TryGetValue(streamId, out var writer) || writer != activationId)
                throw ActivationError("stream_writer_conflict", "Activation does not hold this Fact Stream writer lease.");
            if (factIds is null || factIds.Count == 0 || factIds.Count > _options.MaxFactsPerBatch ||
                factIds.Any(id => id == Guid.Empty) || factIds.Distinct().Count() != factIds.Count)
                throw ActivationError("batch_limit_exceeded",
                    $"facts.recover requires 1 to {_options.MaxFactsPerBatch} distinct non-empty FactIds.");

            ExternalHostFactRecovery? result = null;
            if (!activation.Session.TryCommitAcknowledgement(() =>
                {
                    var stream = _state.Streams.Single(item => item.StreamId == streamId);
                    var ids = factIds.ToHashSet();
                    var saved = _state.Facts.Where(fact => fact.Kind is null &&
                        fact.StreamId == streamId && ids.Contains(fact.FactId)).ToDictionary(fact => fact.FactId);
                    var snapshots = new List<FactSubmission>();
                    var missing = new List<Guid>();
                    foreach (var id in factIds)
                    {
                        if (!saved.TryGetValue(id, out var fact))
                        {
                            missing.Add(id);
                            continue;
                        }
                        FactTime time = stream.FactKind == FactKind.Segment
                            ? new SegmentFactTime(fact.Start, fact.End, fact.IsFinal)
                            : new EventFactTime(fact.OccurredAt!.Value);
                        snapshots.Add(new FactSubmission(fact.StreamId, fact.FactId, fact.Revision,
                            fact.ObservedAt, time, fact.Payload!.Value.Clone(), fact.CollectorId,
                            fact.Foi, fact.Aspect, ObservationContent.Copy(fact.Relations), fact.Kind, fact.Source));
                    }
                    result = new ExternalHostFactRecovery(snapshots, missing);
                }))
                throw ActivationError("activation_stopping", "ExternalHost delivery has been fenced.");
            return result!;
        }
    }
}

internal sealed record ExternalHostFactRecovery(IReadOnlyList<FactSubmission> Facts, IReadOnlyList<Guid> Missing);
