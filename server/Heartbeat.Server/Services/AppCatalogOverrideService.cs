using System.Text.Json;
using Heartbeat.Core;
using Heartbeat.Server.AppCatalog;
using Heartbeat.Server.Data;
using Heartbeat.Server.Entities;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Services;

public sealed class AppCatalogOverrideException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class AppCatalogOverrideService(
    AppDbContext db,
    AppProductReconciliationService products,
    AppCatalogRuntimeSnapshot runtimeCatalog,
    TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public Task<AppProductReconciliationResult> PreviewAsync(
        string identityKey,
        string targetAppKey,
        string? newAppDisplayName,
        string actorSubject,
        CancellationToken cancellationToken = default)
        => SetCoreAsync(
            identityKey, targetAppKey, newAppDisplayName, actorSubject,
            dryRun: true, cancellationToken);

    public Task<AppProductReconciliationResult> SetAsync(
        string identityKey,
        string targetAppKey,
        string? newAppDisplayName,
        string actorSubject,
        CancellationToken cancellationToken = default)
        => SetCoreAsync(
            identityKey, targetAppKey, newAppDisplayName, actorSubject,
            dryRun: false, cancellationToken);

    public async Task<AppProductReconciliationResult> DeleteAsync(
        string identityKey,
        string actorSubject,
        CancellationToken cancellationToken = default)
    {
        EnsureWritableCatalog();
        var normalizedIdentity = NormalizeIdentity(identityKey);
        var actor = NormalizeActor(actorSubject);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await AppCatalogLock.AcquireAsync(db, cancellationToken);

        var localOverride = await db.AppCatalogOverrides
            .Include(x => x.AppIdentity)
            .Include(x => x.TargetApp)
            .SingleOrDefaultAsync(
                x => x.Status == AppCatalogOverrideStatuses.Active &&
                     x.AppIdentity.Key == normalizedIdentity,
                cancellationToken)
            ?? throw new AppCatalogOverrideException(
                "override_not_found", $"Active Override for '{normalizedIdentity}' was not found.");

        var now = _clock.GetUtcNow();
        var previousTargetAppKey = localOverride.TargetAppKey;
        localOverride.Status = AppCatalogOverrideStatuses.Deleted;
        localOverride.UpdatedBySubject = actor;
        localOverride.UpdatedAt = now;
        localOverride.TargetApp = null;
        localOverride.TargetAppId = null;
        await db.SaveChangesAsync(cancellationToken);

        AppProductReconciliationResult result;
        string fallback;
        if (runtimeCatalog.TryGetProduct(normalizedIdentity, out var builtIn))
        {
            var canonicalTarget = await db.Apps.SingleOrDefaultAsync(
                x => x.Key == builtIn.Key, cancellationToken);
            if (canonicalTarget?.IsProvisional == true)
            {
                var belongsToCatalogProduct = await db.AppIdentities.AnyAsync(
                    x => x.AppId == canonicalTarget.Id && builtIn.Identities.Contains(x.Key),
                    cancellationToken);
                if (!belongsToCatalogProduct)
                    throw new AppCatalogException(
                        $"Catalog product key '{builtIn.Key}' is occupied by an unrelated provisional App.");
            }
            if (canonicalTarget is null)
            {
                canonicalTarget = new App
                {
                    Key = builtIn.Key,
                    DisplayName = builtIn.DisplayName,
                    IsProvisional = false
                };
                db.Apps.Add(canonicalTarget);
            }
            result = await products.ReconcileAsync(
                builtIn, [normalizedIdentity], canonicalTarget,
                cancellationToken: cancellationToken);
            fallback = "catalog";
        }
        else
        {
            var provisionalKey = await AllocateProvisionalKeyAsync(normalizedIdentity, cancellationToken);
            var provisional = new App
            {
                Key = provisionalKey,
                DisplayName = DisplayNameFor(normalizedIdentity),
                IsProvisional = true
            };
            db.Apps.Add(provisional);
            var product = new AppCatalogProduct(
                provisional.Key, provisional.DisplayName, [normalizedIdentity]);
            result = await products.ReconcileAsync(
                product, [normalizedIdentity], provisional,
                markTargetFormal: false, cancellationToken: cancellationToken);
            fallback = "provisional";
        }

        db.AppCatalogAudits.Add(new AppCatalogAudit
        {
            EventType = "override-deleted",
            ActorSubject = actor,
            OccurredAt = now,
            SummaryJson = JsonSerializer.Serialize(new
            {
                overrideId = localOverride.Id,
                identityKey = normalizedIdentity,
                previousTargetAppKey,
                fallback,
                targetAppKey = result.TargetAppKey
            })
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result with { TargetAppId = result.TargetAppId == 0 ? localOverride.AppIdentity.AppId : result.TargetAppId };
    }

    private async Task<AppProductReconciliationResult> SetCoreAsync(
        string identityKey,
        string targetAppKey,
        string? newAppDisplayName,
        string actorSubject,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        EnsureWritableCatalog();
        var normalizedIdentity = NormalizeIdentity(identityKey);
        var normalizedTarget = NormalizeProductKey(targetAppKey);
        var actor = NormalizeActor(actorSubject);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await AppCatalogLock.AcquireAsync(db, cancellationToken);

        var identity = await db.AppIdentities
            .Include(x => x.App)
            .SingleOrDefaultAsync(x => x.Key == normalizedIdentity, cancellationToken)
            ?? throw new AppCatalogOverrideException(
                "identity_not_found", $"AppIdentity '{normalizedIdentity}' was not found.");
        var active = await db.AppCatalogOverrides
            .Include(x => x.TargetApp)
            .SingleOrDefaultAsync(
                x => x.AppIdentityId == identity.Id && x.Status == AppCatalogOverrideStatuses.Active,
                cancellationToken);
        if (active is not null && string.Equals(active.TargetAppKey, normalizedTarget, StringComparison.Ordinal))
            throw new AppCatalogOverrideException(
                "same_target", $"AppIdentity '{normalizedIdentity}' already targets '{normalizedTarget}'.");

        var target = await db.Apps.SingleOrDefaultAsync(x => x.Key == normalizedTarget, cancellationToken);
        if (target is null)
        {
            if (string.IsNullOrWhiteSpace(newAppDisplayName))
                throw new AppCatalogOverrideException(
                    "target_not_found", $"Target App '{normalizedTarget}' was not found.");
            target = new App
            {
                Key = normalizedTarget,
                DisplayName = newAppDisplayName.Trim(),
                IsProvisional = false
            };
            db.Apps.Add(target);
        }
        else if (active is null && identity.AppId == target.Id)
        {
            throw new AppCatalogOverrideException(
                "same_target", $"AppIdentity '{normalizedIdentity}' already targets '{normalizedTarget}'.");
        }

        var product = new AppCatalogProduct(target.Key, target.DisplayName, [normalizedIdentity]);
        var now = _clock.GetUtcNow();
        var eventType = active is null ? "override-created" : "override-updated";
        var previousTarget = active?.TargetAppKey;
        if (active is null)
        {
            active = new AppCatalogOverride
            {
                AppIdentity = identity,
                TargetApp = target,
                TargetAppKey = target.Key,
                Status = AppCatalogOverrideStatuses.Active,
                CreatedBySubject = actor,
                UpdatedBySubject = actor,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.AppCatalogOverrides.Add(active);
        }
        else
        {
            active.TargetApp = target;
            active.TargetAppKey = target.Key;
            active.UpdatedBySubject = actor;
            active.UpdatedAt = now;
        }

        // Persist active intent before reconciliation so the database query that protects
        // active Override targets sees the new target, not the superseded one. Dry-run uses
        // the same writes inside a transaction and rolls them all back below.
        await db.SaveChangesAsync(cancellationToken);
        IReadOnlySet<string>? preserveSourceAppKeys = null;
        if (runtimeCatalog.TryGetProduct(normalizedIdentity, out var builtInProduct))
            preserveSourceAppKeys = new HashSet<string>([builtInProduct.Key], StringComparer.Ordinal);
        var result = await products.ReconcileAsync(
            product, [normalizedIdentity], target,
            markTargetFormal: true,
            preserveSourceAppKeys: preserveSourceAppKeys,
            cancellationToken: cancellationToken);
        if (dryRun)
        {
            await transaction.RollbackAsync(cancellationToken);
            db.ChangeTracker.Clear();
            return result;
        }

        db.AppCatalogAudits.Add(new AppCatalogAudit
        {
            EventType = eventType,
            ActorSubject = actor,
            OccurredAt = now,
            SummaryJson = JsonSerializer.Serialize(new
            {
                identityKey = normalizedIdentity,
                previousTargetAppKey = previousTarget,
                targetAppKey = normalizedTarget
            })
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result with { TargetAppId = result.TargetAppId == 0 ? target.Id : result.TargetAppId };
    }

    private async Task<string> AllocateProvisionalKeyAsync(
        string identityKey,
        CancellationToken cancellationToken)
    {
        var candidate = AppIdentityKeys.ProvisionalProductKey(identityKey);
        if (!await db.Apps.AnyAsync(x => x.Key == candidate, cancellationToken)) return candidate;
        var qualified = AppIdentityKeys.QualifiedProductKey(identityKey);
        if (!await db.Apps.AnyAsync(x => x.Key == qualified, cancellationToken)) return qualified;
        for (var suffix = 2; ; suffix++)
        {
            var unique = $"{qualified}-{suffix}";
            if (!await db.Apps.AnyAsync(x => x.Key == unique, cancellationToken)) return unique;
        }
    }

    private static string NormalizeIdentity(string value)
    {
        try { return AppIdentityKeys.Normalize(value); }
        catch (ArgumentException exception)
        {
            throw new AppCatalogOverrideException("invalid_identity", exception.Message);
        }
    }

    private static string NormalizeProductKey(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized != AppIdentityKeys.ProductSlug(normalized))
            throw new AppCatalogOverrideException(
                "invalid_app_key", $"App Key '{value}' must be a normalized lowercase product slug.");
        return normalized;
    }

    private static string NormalizeActor(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new AppCatalogOverrideException("invalid_actor", "Administrator subject is required.");
        return value.Trim();
    }

    private static string DisplayNameFor(string identityKey)
    {
        var value = identityKey[(identityKey.IndexOf(':') + 1)..];
        return identityKey.StartsWith(AppIdentityKeys.MacPrefix, StringComparison.Ordinal)
            ? value.Split('.').Last()
            : value;
    }

    private void EnsureWritableCatalog()
    {
        if (runtimeCatalog.IsRollbackCompatible)
            throw new AppCatalogOverrideException(
                "catalog_rollback_compatibility",
                "App Catalog Overrides cannot be changed while the server is in rollback compatibility mode.");
    }
}
