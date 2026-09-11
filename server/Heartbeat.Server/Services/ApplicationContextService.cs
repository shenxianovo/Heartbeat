using Heartbeat.Server.Data;
using Heartbeat.Server.Entities;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Services;

/// <summary>Context identity and reference maintenance inside the caller's Catalog transaction/lock.</summary>
public sealed class ApplicationContextService(AppDbContext db)
{
    public async Task<ApplicationContextRecord> ResolveAsync(string ownerId, long deviceId, long appId, CancellationToken ct = default)
    {
        var existing = await db.ApplicationContexts.SingleOrDefaultAsync(c =>
            c.OwnerId == ownerId && c.DeviceId == deviceId && c.AppId == appId, ct);
        if (existing is not null) return existing;
        var context = new ApplicationContextRecord { OwnerId = ownerId, DeviceId = deviceId, AppId = appId };
        db.ApplicationContexts.Add(context);
        await db.SaveChangesAsync(ct);
        return context;
    }
    /// <summary>Only facts supported by moved platform identities follow an identity correction.</summary>
    public async Task RebindAsync(long[] identityIds, long targetAppId, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO "ApplicationContexts" ("OwnerId", "DeviceId", "AppId")
            SELECT DISTINCT c."OwnerId", c."DeviceId", {0}
            FROM "Facts" f JOIN "ApplicationContexts" c ON c."Id" = f."TargetId" AND c."OwnerId" = f."OwnerId"
            WHERE f."TargetKind" = 'application-context' AND f."AppIdentityId" = ANY({1})
            ON CONFLICT ("OwnerId", "DeviceId", "AppId") DO NOTHING;
            UPDATE "Facts" f SET "TargetId" = target."Id"
            FROM "ApplicationContexts" old, "ApplicationContexts" target
            WHERE f."TargetKind" = 'application-context' AND f."AppIdentityId" = ANY({1})
              AND old."Id" = f."TargetId" AND old."OwnerId" = f."OwnerId"
              AND target."OwnerId" = old."OwnerId" AND target."DeviceId" = old."DeviceId" AND target."AppId" = {0};
            """;
        await db.Database.ExecuteSqlRawAsync(sql, [targetAppId, identityIds], ct);
        // SQL changed only Target references. Avoid stale tracked facts overwriting them later.
        foreach (var entry in db.ChangeTracker.Entries<IFactRecord>().Where(e =>
                     e.Entity.AppIdentityId is { } id && identityIds.Contains(id)).ToList())
            await entry.ReloadAsync(ct);
    }

    public async Task RemoveUnreferencedAsync(long[] appIds, CancellationToken ct = default)
    {
        var unused = await db.ApplicationContexts.Where(c => appIds.Contains(c.AppId) &&
            !db.Segments.Any(f => f.OwnerId == c.OwnerId && f.TargetKind == "application-context" && f.TargetId == c.Id) &&
            !db.Events.Any(f => f.OwnerId == c.OwnerId && f.TargetKind == "application-context" && f.TargetId == c.Id)).ToListAsync(ct);
        db.ApplicationContexts.RemoveRange(unused);
        await db.SaveChangesAsync(ct);
    }
}
