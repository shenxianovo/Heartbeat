using System.Text.Json;
using Heartbeat.Collection.Hub.Collectors.Protocol;
using Heartbeat.Collection.Hub.Collectors.Packages;
using Heartbeat.Collection.Hub.Upload;
using Heartbeat.Core.DTOs.Facts;
using Serilog;

namespace Heartbeat.Collection.Hub.Collectors.Runtime;

public sealed partial class CollectorRuntime
{
    public DeliveryRemainder FactUploadRemainder
    {
        get
        {
            lock (_gate)
                return new DeliveryRemainder(
                    _state.Facts.Count(fact => !fact.Delivered) + _state.Gaps.Count(gap => !gap.Delivered),
                    0);
        }
    }

    /// <summary>Returns a bounded snapshot without releasing durable ownership.</summary>
    public List<FactUploadItem> ReadPendingFacts()
    {
        lock (_gate)
        {
            var streams = _state.Streams.ToDictionary(stream => stream.StreamId);
            var items = new List<FactUploadItem>();
            // Reserve room for Gaps so a continuous Event stream cannot starve loss reports.
            foreach (var gap in _state.Gaps.Where(gap => !gap.Delivered).Take(Math.Max(1, _options.MaxFactsPerBatch / 4)))
                items.Add(new FactUploadItem(UploadStreamDefinition(streams[gap.StreamId]), null,
                    UploadGap(gap)));
            foreach (var fact in _state.Facts.Where(fact => !fact.Delivered)
                         .Take(_options.MaxFactsPerBatch - items.Count))
                items.Add(UploadFact(streams[fact.StreamId], fact));
            return items;
        }
    }

    /// <summary>A late response must never acknowledge a newer local Revision.</summary>
    public void ConfirmUploadedFacts(IReadOnlyList<FactUploadItem> items)
    {
        lock (_gate)
        {
            var facts = items.Where(item => item.Fact is not null)
                .Select(item => item.Fact!)
                .ToLookup(item => (item.StreamId, item.FactId, item.Revision));
            var gaps = items.Where(item => item.Gap is not null)
                .Select(item => item.Gap!).ToLookup(item => (item.StreamId, item.GapId));
            var next = new CollectorRuntimeState
            {
                SchemaVersion = _state.SchemaVersion,
                Instances = [.. _state.Instances], Streams = [.. _state.Streams],
                ActivationAttemptTombstones = [.. _state.ActivationAttemptTombstones],
                Facts = _state.Facts.Select(fact => facts[(fact.StreamId, fact.FactId, fact.Revision)].Any(item =>
                    item.CollectorId == fact.CollectorId && item.Foi == fact.Foi && item.Aspect == fact.Aspect && Heartbeat.Core.Facts.ObservationContent.Equal(item.Relations, fact.Relations) &&
                    item.Start == (fact.OccurredAt is null ? fact.Start : null) && item.End == (fact.OccurredAt is null ? fact.End : null) &&
                    item.OccurredAt == fact.OccurredAt && item.IsFinal == (fact.OccurredAt is null ? fact.IsFinal : null) &&
                    item.Payload is { } payload && fact.Payload is { } saved && JsonElement.DeepEquals(payload, saved))
                    ? fact.ConfirmDelivery() : fact).ToList(),
                Gaps = _state.Gaps.Select(gap => gaps[(gap.StreamId, gap.GapId)].Any(item => item.Start == gap.Start &&
                    item.End == gap.End && item.Reason == gap.Reason && item.EstimatedFactsLost == gap.EstimatedFactsLost)
                    ? gap.ConfirmDelivery() : gap).ToList()
            };
            _store.Save(next);
            _state = next;
        }
    }

    private bool HasPendingFactsLocked(Guid instanceId)
    {
        var streams = _state.Streams.Where(stream => stream.CollectorInstanceId == instanceId)
            .Select(stream => stream.StreamId).ToHashSet();
        return _state.Facts.Any(fact => streams.Contains(fact.StreamId) && !fact.Delivered) ||
               _state.Gaps.Any(gap => streams.Contains(gap.StreamId) && !gap.Delivered);
    }

    private bool CanEvictDeliveredFact(CommittedFactState fact)
    {
        if (!fact.Delivered) return false;
        var stream = _state.Streams.Single(candidate => candidate.StreamId == fact.StreamId);
        if (stream.FactKind == FactKind.Segment)
            return fact.IsFinal;
        return true;
    }

    private void EnsureUploadGapIdentities()
    {
        if (_state.Gaps.All(gap => gap.GapId != Guid.Empty)) return;
        var next = _state;
        foreach (var gap in _state.Gaps.Where(gap => gap.GapId == Guid.Empty))
            next = next.WithBoundGapIdentity(gap, Guid.CreateVersion7(), awaitingLegacyIdentity: true);
        // Identity allocation is durable before any upload, so a lost HTTP ACK repeats the same GapId.
        _store.Save(next);
        _state = next;
    }

    private void ObserveCommittedFact(FactStreamState stream, CommittedFactState fact)
    {
        if (_segmentSink is not ICollectorFactObserver observer) return;
        try { observer.Observe(UploadFact(stream, fact)); }
        catch (Exception exception)
        {
            Log.Warning(exception, "Collector host read model failed; durable Fact {FactId} remains accepted", fact.FactId);
        }
    }

    private static FactUploadItem UploadFact(FactStreamState stream, CommittedFactState fact) => new(
        UploadStreamDefinition(stream),
        new FactSnapshot
        {
            StreamId = fact.StreamId, FactId = fact.FactId, Revision = fact.Revision,
            CollectorId = fact.CollectorId, Foi = fact.Foi, Aspect = fact.Aspect, Relations = Heartbeat.Core.Facts.ObservationContent.Copy(fact.Relations),
            ObservedAt = fact.ObservedAt,
            Start = stream.FactKind == FactKind.Segment ? fact.Start : null,
            End = stream.FactKind == FactKind.Segment ? fact.End : null,
            IsFinal = stream.FactKind == FactKind.Segment ? fact.IsFinal : null,
            OccurredAt = fact.OccurredAt, Payload = fact.Payload?.Clone()
        }, null);

    private static FactStreamDefinition UploadStreamDefinition(FactStreamState stream) => new()
    {
        StreamId = stream.StreamId, CollectorInstanceId = stream.CollectorInstanceId,
        Subject = new FactSubject
        {
            SubjectId = stream.SubjectId, Kind = stream.SubjectKind.ToString().ToLowerInvariant(),
            HardwareId = stream.SubjectKind == SubjectKind.Machine ? stream.SubjectId.ToString("D") : null
        },
        OutputId = stream.OutputId, Source = stream.Source, FactKind = stream.FactKind.ToString().ToLowerInvariant(),
        Dimensions = new Dictionary<string, string>(stream.Dimensions, StringComparer.Ordinal),
    };

    private static FactGapSnapshot UploadGap(CommittedGapState gap) => new()
    {
        StreamId = gap.StreamId, GapId = gap.GapId, Start = gap.Start, End = gap.End,
        Reason = gap.Reason, EstimatedFactsLost = gap.EstimatedFactsLost
    };

}
