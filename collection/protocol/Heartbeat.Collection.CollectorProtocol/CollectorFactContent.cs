using System.Text.Json;
using Heartbeat.Core.Facts;
using Heartbeat.Core.DTOs.Facts;

namespace Heartbeat.Collection.CollectorProtocol;

internal static class CollectorFactContent
{
    public static CollectorFact Capture(CollectorFact fact)
    {
        if (fact.Kind is not null)
        {
            var snapshot = new ObservationSnapshot
            {
                Id = fact.FactId, Kind = fact.Kind, CollectorId = fact.CollectorId ?? Guid.Empty,
                Foi = fact.Foi, Aspect = fact.Aspect, Revision = fact.Revision,
                Result = fact.Payload, Relations = fact.Relations ?? [], Source = fact.Source,
                Start = (fact.Time as CollectorSegmentFactTime)?.Start,
                End = (fact.Time as CollectorSegmentFactTime)?.End,
                OccurredAt = (fact.Time as CollectorEventFactTime)?.OccurredAt
            };
            if (ObservationValidation.Validate(snapshot, DateTimeOffset.UtcNow) is { } error)
                throw new ArgumentException(error);
        }
        return fact with
        {
            Payload = fact.Payload.Clone(),
            Relations = fact.Relations is null && fact.Kind is null ? null : ObservationContent.Copy(fact.Relations)
        };
    }

    public static void ValidateRevision(CollectorFact previous, CollectorFact incoming)
    {
        if (previous.Kind is null && incoming.Kind is null)
            return;
        if (incoming.Revision < previous.Revision)
            return;
        if (previous.Kind != incoming.Kind || previous.CollectorId != incoming.CollectorId ||
            previous.Foi != incoming.Foi || previous.Aspect != incoming.Aspect ||
            (previous.Time, incoming.Time) switch
            {
                (CollectorSegmentFactTime first, CollectorSegmentFactTime second) =>
                    first.Start != second.Start || first.IsFinal && !second.IsFinal,
                (CollectorEventFactTime first, CollectorEventFactTime second) => first.OccurredAt != second.OccurredAt,
                _ => true
            })
            throw new ArgumentException("Fact Revision cannot change Collector, FOI, Kind, Aspect, start/occurrence or reopen a final Segment.");
        if (previous.Revision == incoming.Revision &&
            (previous.Time != incoming.Time || previous.ObservedAt != incoming.ObservedAt ||
             previous.Source != incoming.Source || !JsonElement.DeepEquals(previous.Payload, incoming.Payload) ||
             !ObservationContent.Equal(previous.Relations, incoming.Relations)))
            throw new ArgumentException("The same Fact Revision has different content.");
    }
}
