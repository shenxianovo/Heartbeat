using Heartbeat.Core;
using Heartbeat.Core.DTOs.Facts;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Services;

public sealed partial class FactStore
{
    // A stale revision still has to be a valid native observation. Validate without registering
    // objects, so an ignored replay cannot create new private objects or provisional products.
    private async Task ValidateNativeReferences(string owner, ObservationSnapshot snapshot, CancellationToken ct)
    {
        var references = new List<ObservationObjectReference> { snapshot.Foi! };
        if (snapshot.Relations.Count > 8) throw new FactIngestException("Too many observation relations.");
        foreach (var relation in snapshot.Relations)
        {
            if (relation?.Members is null || relation.Members.Any(m => m?.Object is null))
                throw new FactIngestException("Invalid observation relation.");
            references.AddRange(relation.Members.Select(m => m.Object));
        }
        foreach (var reference in references)
        {
            ValidateReferenceShape(reference);
            switch (reference.Kind)
            {
                case "machine":
                    if (reference.Scope != ObservationObjectScopes.Machine || reference.Key.Length > 256 ||
                        reference.Key.StartsWith("subject:", StringComparison.Ordinal))
                        throw new FactIngestException("A machine requires an observed device identity.");
                    break;
                case "app":
                    if (reference.Scope == ObservationObjectScopes.AppIdentity)
                    {
                        try { _ = AppIdentityKeys.Normalize(reference.Key); }
                        catch (ArgumentException ex) { throw new FactIngestException(ex.Message); }
                    }
                    else if (reference.Scope != ObservationObjectScopes.App || reference.Key.Length > 128)
                        throw new FactIngestException("Invalid App identity scope or product key.");
                    break;
                case "person":
                    if (reference.Scope != ObservationObjectScopes.Person || !Guid.TryParse(reference.Key, out var person) ||
                        !await db.Persons.AnyAsync(p => p.OwnerId == owner && p.Reference == person, ct))
                        throw new FactIngestException("Person Object must already exist within its Owner.");
                    break;
                case "account":
                    if (reference.Scope == "vrchat" && !ServiceAccountReference.IsVRChatAccountId(reference.Key))
                        throw new FactIngestException("A native VRChat observation requires its real account identity.");
                    break;
            }
        }
    }
    private static void ValidateReferenceShape(ObservationObjectReference reference)
    {
        if (reference.Kind is not ("machine" or "app" or "account" or "person") ||
            string.IsNullOrWhiteSpace(reference.Scope) || reference.Scope.Length > 128 || reference.Scope != reference.Scope.Trim() ||
            string.IsNullOrWhiteSpace(reference.Key) || reference.Key.Length > 1024 || reference.Key != reference.Key.Trim())
            throw new FactIngestException("Invalid Object reference.");
    }

}
