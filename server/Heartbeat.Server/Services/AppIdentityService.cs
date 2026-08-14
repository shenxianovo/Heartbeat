using Heartbeat.Core;
using Heartbeat.Server.AppCatalog;
using Heartbeat.Server.Data;
using Heartbeat.Server.Entities;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Services;

/// <summary>
/// 解析平台观测身份。Catalog 内身份直接绑定规范产品；目录外身份创建一对一
/// provisional App。相似名称和短键绝不作为产品归并证据。
/// </summary>
public class AppIdentityService(AppDbContext db, AppCatalogRuntimeSnapshot? catalog = null)
{
    public async Task<AppIdentity> ResolveAsync(
        string identityKey,
        string? observedDisplayName = null,
        CancellationToken cancellationToken = default)
    {
        var normalized = AppIdentityKeys.Normalize(identityKey);
        var existing = await db.AppIdentities
            .Include(x => x.App)
            .SingleOrDefaultAsync(x => x.Key == normalized, cancellationToken);
        if (existing != null) return existing;

        // Serialize all previously unseen identities with Catalog reconciliation and Override
        // mutations. This also closes the pre-existing race where segment/presence/icon could
        // concurrently create two provisional products for the same identity.
        var ownsTransaction = db.Database.CurrentTransaction is null;
        await using var transaction = ownsTransaction
            ? await db.Database.BeginTransactionAsync(cancellationToken)
            : null;
        await AppCatalogLock.AcquireAsync(db, cancellationToken);

        existing = await db.AppIdentities
            .Include(x => x.App)
            .SingleOrDefaultAsync(x => x.Key == normalized, cancellationToken);
        if (existing is not null)
        {
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
            return existing;
        }

        App app;
        if (catalog?.TryGetProduct(normalized, out var knownProduct) == true)
        {
            var canonical = await db.Apps.SingleOrDefaultAsync(
                x => x.Key == knownProduct.Key, cancellationToken);
            if (canonical?.IsProvisional == true)
                throw new AppCatalogException(
                    $"Catalog product key '{knownProduct.Key}' is occupied by an unrelated provisional App.");
            app = canonical ?? new App
                {
                    Key = knownProduct.Key,
                    DisplayName = knownProduct.DisplayName,
                    IsProvisional = false
                };
            app.DisplayName = knownProduct.DisplayName;
            app.IsProvisional = false;
        }
        else
        {
            app = new App
            {
                Key = await AllocateProductKeyAsync(normalized, cancellationToken),
                DisplayName = ResolveDisplayName(normalized, observedDisplayName),
                IsProvisional = true
            };
        }
        var identity = new AppIdentity { Key = normalized, App = app };
        db.AppIdentities.Add(identity);
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        return identity;
    }

    private async Task<string> AllocateProductKeyAsync(string identityKey, CancellationToken cancellationToken)
    {
        var candidate = AppIdentityKeys.ProvisionalProductKey(identityKey);
        if (!await db.Apps.AnyAsync(x => x.Key == candidate, cancellationToken))
            return candidate;

        var qualified = AppIdentityKeys.QualifiedProductKey(identityKey);
        if (!await db.Apps.AnyAsync(x => x.Key == qualified, cancellationToken))
            return qualified;

        for (var suffix = 2; ; suffix++)
        {
            var unique = $"{qualified}-{suffix}";
            if (!await db.Apps.AnyAsync(x => x.Key == unique, cancellationToken))
                return unique;
        }
    }

    private static string ResolveDisplayName(string identityKey, string? observedDisplayName)
    {
        if (!string.IsNullOrWhiteSpace(observedDisplayName))
            return observedDisplayName.Trim();

        var value = identityKey[(identityKey.IndexOf(':') + 1)..];
        return identityKey.StartsWith(AppIdentityKeys.MacPrefix, StringComparison.Ordinal)
            ? value.Split('.').Last()
            : value;
    }
}
