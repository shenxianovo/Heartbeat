using Heartbeat.Server.Data;
using Heartbeat.Server.Entities;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Services;

/// <summary>Product corrections follow the exact platform evidence inside the Catalog transaction.</summary>
public sealed class AppObservationService(AppDbContext db)
{
    public async Task MergeAsync(long sourceAppId, long targetAppId, CancellationToken ct = default)
    {
        await db.Database.ExecuteSqlRawAsync("""
            UPDATE "Facts" f SET "FoiId" = target."ObjectId"
            FROM "Apps" source, "Apps" target
            WHERE source."Id" = {0} AND target."Id" = {1} AND f."FoiId" = source."ObjectId";
            UPDATE "RelationMembers" m SET "ObjectId" = target."ObjectId"
            FROM "Apps" source, "Apps" target
            WHERE source."Id" = {0} AND target."Id" = {1} AND m."Role" = 'app' AND m."ObjectId" = source."ObjectId";
            """, [sourceAppId, targetAppId], ct);
        await ReloadAsync(ct);
    }

    public async Task RebindAsync(long[] identityIds, long targetAppId, CancellationToken ct = default)
    {
        await db.Database.ExecuteSqlRawAsync("""
            UPDATE "Facts" f SET "FoiId" = target."ObjectId"
            FROM "Objects" old, "Apps" target
            WHERE f."AppIdentityId" = ANY({0}) AND old."Id" = f."FoiId" AND old."Kind" = 'app' AND target."Id" = {1};
            UPDATE "RelationMembers" m SET "ObjectId" = target."ObjectId"
            FROM "Relations" r, "Facts" f, "Apps" target
            WHERE m."RelationId" = r."Id" AND r."FactId" = f."Id" AND r."OwnerId" = f."OwnerId"
              AND f."AppIdentityId" = ANY({0}) AND m."Role" = 'app' AND target."Id" = {1};
            """, [identityIds, targetAppId], ct);
        await ReloadAsync(ct);
    }

    private async Task ReloadAsync(CancellationToken ct)
    {
        foreach (var entry in db.ChangeTracker.Entries<FactRecord>().ToList()) await entry.ReloadAsync(ct);
        // Member object ids are part of the key: detach the replaced identity before loading the new graph.
        var relations = db.ChangeTracker.Entries<ObjectRelation>().Select(e => e.Entity).ToArray();
        foreach (var entry in db.ChangeTracker.Entries<RelationMember>().ToList()) entry.State = EntityState.Detached;
        foreach (var relation in relations)
        {
            relation.Members.Clear();
            var members = db.Entry(relation).Collection(r => r.Members);
            members.IsLoaded = false;
            await members.LoadAsync(ct);
        }
    }
}
