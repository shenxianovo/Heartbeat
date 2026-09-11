using Heartbeat.Collection.Hub.Collectors.Protocol;
using Heartbeat.Core.Facts;

namespace Heartbeat.Collection.Hub.Collectors.Runtime;

public sealed partial class CollectorRuntime
{
    private bool CanPublishFact(Guid activationId, FactSubmission fact)
    {
        if (fact.Kind is null)
            return _streamWriters.TryGetValue(fact.StreamId, out var writer) && writer == activationId;
        var session = _activations.GetValueOrDefault(activationId)?.Session ??
            _externalHostActivations.GetValueOrDefault(activationId)?.Session;
        return session?.SelectedCapabilities.GetValueOrDefault("facts.observation") == 2 &&
            _state.ActivationAttemptTombstones.Any(attempt => attempt.ActivationId == activationId &&
                attempt.CollectorInstanceId != Guid.Empty) &&
            (fact.StreamId == Guid.Empty ||
             _streamWriters.TryGetValue(fact.StreamId, out var nativeWriter) && nativeWriter == activationId);
    }

    private FactDeliveryOutcome? PrepareObservation(Guid activationId, int index, FactSubmission fact,
        FactStreamState? stream, out PreparedFactCommit prepared)
    {
        prepared = default!;
        var deliveryInstanceId = _state.ActivationAttemptTombstones.Single(attempt => attempt.ActivationId == activationId).CollectorInstanceId;
        if (ObservationValidation.Validate(fact.ToObservation(), _options.TimeProvider.GetUtcNow()) is { } error)
            return Rejected(index, "fact_invalid", error);
        if (fact.ObservedAt is { Offset: var offset } && offset != TimeSpan.Zero)
            return Rejected(index, "fact_invalid", "Fact observedAt must be UTC.");
        var current = _state.Facts.SingleOrDefault(saved => saved.Kind is not null && saved.FactId == fact.FactId);
        if (current is not null && (current.CollectorId != fact.CollectorId || current.DeliveryInstanceId != deliveryInstanceId))
            return Rejected(index, "fact_revision_conflict", "Fact identity is unavailable.");
        if ((fact.Kind == "segment" ? ValidateSegmentContent(fact) : ValidateEventContent(fact, null)) is { } timeError)
            return Rejected(index, "fact_invalid", timeError);
        if (current is not null)
        {
            if (current.Kind != fact.Kind)
                return Rejected(index, "fact_revision_conflict", "Fact Kind cannot change.");
            if (fact.Revision < current.Revision)
                return new FactDeliveryOutcome(index, FactDeliveryStatus.Superseded);
            if (fact.Revision == current.Revision)
                return SameContent(current, fact)
                    ? new FactDeliveryOutcome(index, FactDeliveryStatus.Duplicate)
                    : Rejected(index, "fact_revision_conflict", "The same Fact Revision has different content.");
            if (current.Foi != fact.Foi || current.Aspect != fact.Aspect ||
                current.Start != (fact.Time.Start ?? default) || current.OccurredAt != fact.Time.OccurredAt ||
                current.IsFinal && fact.Time.IsFinal != true)
                return Rejected(index, "fact_revision_conflict", "Fact Revision cannot change Observer, FOI, Kind, Aspect, start/occurrence or reopen a final Segment.");
        }
        CommittedFactState? evicted = null;
        if (current is null && _state.Facts.Count >= _options.MaxDurableFacts)
        {
            evicted = _state.Facts.FirstOrDefault(CanEvictDeliveredFact);
            if (evicted is null) return Retry(index, "Hub pending observation journal is applying backpressure.");
        }
        prepared = new PreparedFactCommit(stream, new CommittedFactState
        {
            // Stream is an optional delivery group. Native identity is always the producer FactId.
            StreamId = fact.StreamId, FactId = fact.FactId, Revision = fact.Revision,
            Kind = fact.Kind, Source = fact.Source, CollectorId = fact.CollectorId, DeliveryInstanceId = deliveryInstanceId,
            Foi = fact.Foi, Aspect = fact.Aspect, Relations = ObservationContent.Copy(fact.Relations),
            ObservedAt = fact.ObservedAt, Start = fact.Time.Start ?? default, End = fact.Time.End ?? default,
            OccurredAt = fact.Time.OccurredAt, IsFinal = fact.Time.IsFinal ?? false, Payload = fact.Payload.Clone()
        }, evicted);
        return null;
    }
}
