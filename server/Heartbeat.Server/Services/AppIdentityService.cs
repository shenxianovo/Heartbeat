using Heartbeat.Core;
using Heartbeat.Server.Data;
using Heartbeat.Server.Entities;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Services;

/// <summary>
/// 解析平台观测身份。未知身份始终创建一对一 provisional App；相似名称和短键绝不
/// 自动绑定到既有产品。显式多身份映射由 Ticket 04 的管理领域操作负责。
/// </summary>
public class AppIdentityService(AppDbContext db)
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

        var app = new App
        {
            Key = await AllocateProductKeyAsync(normalized, cancellationToken),
            DisplayName = ResolveDisplayName(normalized, observedDisplayName),
            IsProvisional = true
        };
        var identity = new AppIdentity { Key = normalized, App = app };
        db.AppIdentities.Add(identity);
        await db.SaveChangesAsync(cancellationToken);
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
