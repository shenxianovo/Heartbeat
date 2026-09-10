using Heartbeat.Core.DTOs.Facts;
using Heartbeat.Server.Data;
using Heartbeat.Server.Entities;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Services;

/// <summary>Account resolution under FactStore's owner and catalog transaction locks.</summary>
public sealed class ServiceAccountService(AppDbContext db)
{
    public async Task<ServiceAccount> ResolveAsync(string ownerId, ServiceAccountReference? reference,
        Guid legacySubjectId, CancellationToken ct)
    {
        var service = reference?.ServiceKey ?? "vrchat";
        if (!await db.ServiceProducts.AnyAsync(s => s.ServiceKey == service, ct))
        {
            var product = await db.Apps.SingleOrDefaultAsync(a => a.Key == "vrchat", ct);
            if (product is null)
            {
                product = new App { Key = "vrchat", DisplayName = "VRChat" };
                db.Apps.Add(product);
            }
            db.ServiceProducts.Add(new ServiceProduct { ServiceKey = service, App = product });
            await db.SaveChangesAsync(ct);
        }
        var accountId = reference?.ServiceAccountId;
        Guid? legacy = reference is null ? legacySubjectId : null;
        var account = await db.ServiceAccounts.SingleOrDefaultAsync(a => a.OwnerId == ownerId &&
            a.ServiceKey == service && a.ServiceAccountId == accountId && a.LegacySubjectId == legacy, ct);
        if (account is not null) return account;
        account = new ServiceAccount { OwnerId = ownerId, ServiceKey = service, ServiceAccountId = accountId, LegacySubjectId = legacy };
        db.ServiceAccounts.Add(account);
        await db.SaveChangesAsync(ct);
        return account;
    }
}
