using System.Text.Json;
using Heartbeat.Core.DTOs.Facts;
using Heartbeat.Core.Facts;
using Heartbeat.Server.Entities;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Services;

public sealed partial class FactStore
{
    public async Task IngestObservationsAsync(string ownerId, ObservationUploadRequest request, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        if (request.Facts is null || request.Facts.Count > 10000)
            throw new FactIngestException("Invalid observation batch.");
        foreach (var snapshot in request.Facts) ValidateObservation(snapshot, _time.GetUtcNow());
        await Atomic(ownerId, async () =>
        {
            foreach (var snapshot in request.Facts)
            {
                // Look up globally: a collision must never adopt another Owner's or legacy identity.
                var fact = await db.Set<FactRecord>().SingleOrDefaultAsync(f => f.Id == snapshot.Id, ct);
                if (fact is not null && (fact.OwnerId != ownerId || fact.StreamId is not null || fact.Kind != snapshot.Kind))
                    throw new FactIngestException("Fact identity is unavailable.", true);
                await ValidateNativeReferences(ownerId, snapshot, ct);
                if (fact is not null && snapshot.Revision < fact.Revision) continue;
                var observation = await ResolveObservation(ownerId, snapshot.CollectorId, snapshot.Foi, snapshot.Relations, ct, fact);
                if (fact is null)
                {
                    fact = snapshot.Kind == "segment" ? new Segment() : new Event();
                    fact.Id = snapshot.Id;
                    fact.OwnerId = ownerId;
                    db.Add(fact);
                }
                await SaveSnapshot(fact, snapshot.Revision, snapshot.Aspect, snapshot.Source,
                    JsonDocument.Parse(snapshot.Result!.Value.GetRawText()), observation,
                    NormalizeTime(snapshot.Start), NormalizeTime(snapshot.End), NormalizeTime(snapshot.OccurredAt), ct);
            }
        }, ct);
    }

    private static void ValidateObservation(ObservationSnapshot snapshot, DateTimeOffset now)
    {
        if (ObservationValidation.Validate(snapshot, now) is { } error)
            throw new FactIngestException(error);
    }

    // Both explicit legacy adaptation and independent inputs commit through this single core.
    private async Task SaveSnapshot(IFactRecord fact, long revision, string? aspect, string? source,
        JsonDocument payload, ObservationState observation, DateTimeOffset? start, DateTimeOffset? end,
        DateTimeOffset? at, CancellationToken ct, bool legacyTakeover = false)
    {
        observation = observation with { AppIdentityId = await CurrentAppIdentity(fact, observation, ct) };
        if (fact.Revision > 0 && !legacyTakeover)
        {
            if (revision < fact.Revision) return;
            var sameTimes = fact is Segment segment
                ? segment.StartTime == start && segment.EndTime == end : ((Event)fact).Timestamp == at;
            if (revision == fact.Revision)
            {
                if (!sameTimes || fact.Aspect != aspect || fact.Source != source || fact.ObserverId != observation.CollectorId ||
                    fact.FoiId != observation.FoiId || fact.AppIdentityId != observation.AppIdentityId ||
                    !await SameRelations(fact.Id, observation.Relations, ct) || !JsonElement.DeepEquals(fact.Payload.RootElement, payload.RootElement))
                    throw new FactIngestException("The same Fact Revision has different content.", true);
                await SaveAppReferences(fact, revision, observation, ct);
                await db.SaveChangesAsync(ct);
                return;
            }
            if (fact is Segment oldSegment ? oldSegment.StartTime != start : ((Event)fact).Timestamp != at)
                throw new FactIngestException("Fact Revision cannot change its start/occurrence.", true);
            // Only explicit legacy inputs can deterministically fill historical unknowns.
            if ((fact.StreamId is null || fact.ObserverId is not null) && fact.ObserverId != observation.CollectorId ||
                (fact.StreamId is null || fact.FoiId is not null) && fact.FoiId != observation.FoiId ||
                (fact.StreamId is null || fact.Aspect is not null) && fact.Aspect != aspect)
                throw new FactIngestException("Fact Revision cannot change Observer, FOI or Aspect.", true);
        }
        await SaveAppReferences(fact, revision, observation, ct);
        fact.Source = source;
        fact.Aspect = aspect;
        fact.ObserverId = observation.CollectorId;
        fact.FoiId = observation.FoiId;
        fact.TargetKind = null;
        fact.TargetId = null;
        fact.Revision = revision;
        fact.Payload = payload;
        fact.AppIdentityId = observation.AppIdentityId;
        if (fact is Segment savedSegment)
        {
            savedSegment.StartTime = start!.Value;
            savedSegment.EndTime = end!.Value;
        }
        else ((Event)fact).Timestamp = at!.Value;
        await db.SaveChangesAsync(ct);
        await WriteRelations(fact, observation.Relations, ct);
    }
}
