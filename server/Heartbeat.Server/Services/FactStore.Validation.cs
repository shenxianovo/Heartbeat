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
        var references = new[] { snapshot.Foi! }.Concat(snapshot.Relations.SelectMany(relation => relation.Members.Select(member => member.Object)));
        foreach (var reference in references.Where(reference => reference.Kind == "person"))
        {
            var person = Guid.Parse(reference.Key); // Shape was checked by shared native validation.
            if (!await db.Persons.AnyAsync(p => p.OwnerId == owner && p.Reference == person, ct))
                throw new FactIngestException("Person Object must already exist within its Owner.");
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
