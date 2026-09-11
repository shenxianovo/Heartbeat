using System.Text.Json;
using Heartbeat.Core.DTOs.Facts;
using Heartbeat.Server.Entities;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Services;

public sealed partial class FactStore
{
    private sealed record ResolvedAppReference(string Position, ObservationObjectReference Reference, Guid ObjectId);
    private sealed record AcceptedAppReference(string Position, ObservationObjectReference Reference, long IdentityId);

    private async Task<long?> CurrentAppIdentity(IFactRecord fact, ObservationState observation, CancellationToken ct)
    {
        if (observation.AppIdentityId is { } suppliedIdentity) return suppliedIdentity;
        if (fact.AppIdentityId is not { } previousIdentity) return null;
        var previousAppObject = await db.AppIdentities.Where(i => i.Id == previousIdentity)
            .Select(i => EF.Property<Guid>(i.App, "ObjectId")).SingleAsync(ct);
        // Platform evidence follows the current App reference, not the Fact forever.
        // A machine may legitimately stop observing one App and relate to another in a later snapshot.
        return observation.FoiId == previousAppObject || observation.Relations.Any(r =>
            r.Members.Any(m => m.Role == "app" && m.ObjectId == previousAppObject)) ? previousIdentity : null;
    }

    private async Task<ObjectResolution> ResolveObservationReference(string owner, ObservationObjectReference reference,
        string position, IFactRecord? existingFact, List<ResolvedAppReference> appReferences, CancellationToken ct)
    {
        ObjectResolution resolved;
        var accepted = ReadAppReferences(existingFact?.AppReferenceEvidence)
            .SingleOrDefault(e => e.Position == position && e.Reference == reference);
        if (accepted is not null)
        {
            // A Catalog correction follows only this precise, previously accepted reference.
            // An arbitrary product key must still resolve normally and pass the immutable FOI checks.
            var currentObject = await db.AppIdentities.Where(i => i.Id == accepted.IdentityId)
                .Select(i => EF.Property<Guid>(i.App, "ObjectId")).SingleAsync(ct);
            resolved = new(currentObject, accepted.IdentityId);
        }
        else resolved = await ResolveObject(owner, reference, ct);
        if (reference.Kind == "app" && reference.Scope == ObservationObjectScopes.App)
            appReferences.Add(new(position, reference, resolved.Id));
        return resolved;
    }

    private async Task SaveAppReferences(IFactRecord fact, long revision, ObservationState observation, CancellationToken ct)
    {
        if (observation.AppReferences is null) return;
        var accepted = revision == fact.Revision ? ReadAppReferences(fact.AppReferenceEvidence) : [];
        if (observation.AppIdentityId is { } identityId)
        {
            var identityObject = await db.AppIdentities.Where(i => i.Id == identityId)
                .Select(i => EF.Property<Guid>(i.App, "ObjectId")).SingleAsync(ct);
            accepted.AddRange(observation.AppReferences.Where(r => r.ObjectId == identityObject)
                .Select(r => new AcceptedAppReference(r.Position, r.Reference, identityId)));
        }
        var evidence = accepted.Distinct().OrderBy(e => e.Position).ThenBy(e => e.Reference.Scope)
            .ThenBy(e => e.Reference.Key).ToList();
        fact.AppReferenceEvidence = evidence.Count == 0 ? null : JsonSerializer.SerializeToDocument(evidence);
    }

    private static List<AcceptedAppReference> ReadAppReferences(JsonDocument? evidence) =>
        evidence?.Deserialize<List<AcceptedAppReference>>() ?? [];
}
