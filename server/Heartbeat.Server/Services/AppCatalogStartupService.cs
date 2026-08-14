using System.Text.Json;
using Heartbeat.Server.AppCatalog;
using Heartbeat.Server.Data;
using Heartbeat.Server.Entities;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Services;

public sealed class AppCatalogStartupService(
    AppDbContext db,
    ILogger<AppCatalogStartupService> logger,
    TimeProvider? clock = null,
    AppProductReconciliationService? productReconciliation = null,
    AppCatalogRuntimeSnapshot? runtimeSnapshot = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly AppProductReconciliationService _products =
        productReconciliation ?? new AppProductReconciliationService(db);

    public async Task<AppCatalogStartupResult> ApplyAsync(
        AppCatalogSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await AppCatalogLock.AcquireAsync(db, cancellationToken);
        var state = await db.AppCatalogStates.SingleOrDefaultAsync(cancellationToken);

        if (state is null)
        {
            var summary = await ReconcileMappingsAsync(snapshot, cancellationToken);
            state = NewState(snapshot);
            db.AppCatalogStates.Add(state);
            db.AppCatalogAudits.Add(NewAppliedAudit(snapshot, summary));
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            runtimeSnapshot?.Enable();
            return AppCatalogStartupResult.Applied;
        }

        if (state.CatalogVersion == snapshot.Document.CatalogVersion &&
            !string.Equals(state.ContentHash, snapshot.ContentHash, StringComparison.Ordinal))
        {
            throw new AppCatalogException(
                $"App Catalog content drift detected for catalogVersion {state.CatalogVersion}: " +
                $"database hash is {state.ContentHash}, binary hash is {snapshot.ContentHash}. " +
                "Increase catalogVersion when changing Catalog content.");
        }

        if (state.CatalogVersion > snapshot.Document.CatalogVersion)
        {
            state.StartupMode = AppCatalogStartupModes.RollbackCompatible;
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            runtimeSnapshot?.EnterRollbackCompatibility();
            logger.LogWarning(
                "App Catalog rollback compatibility mode: database version {DatabaseVersion} is newer than binary version {BinaryVersion}; retaining database mappings and skipping downgrade reconciliation.",
                state.CatalogVersion,
                snapshot.Document.CatalogVersion);
            return AppCatalogStartupResult.RollbackCompatible;
        }

        if (state.CatalogVersion == snapshot.Document.CatalogVersion)
        {
            if (state.SchemaVersion != snapshot.Document.SchemaVersion)
                throw new AppCatalogException(
                    $"App Catalog schema drift detected for catalogVersion {state.CatalogVersion}.");

            await ReconcileMappingsAsync(
                snapshot, cancellationToken, recordCatalogObservations: false);
            state.StartupMode = AppCatalogStartupModes.Normal;
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            runtimeSnapshot?.Enable();
            return AppCatalogStartupResult.AlreadyApplied;
        }

        var reconciliation = await ReconcileMappingsAsync(snapshot, cancellationToken);
        state.SchemaVersion = snapshot.Document.SchemaVersion;
        state.CatalogVersion = snapshot.Document.CatalogVersion;
        state.ContentHash = snapshot.ContentHash;
        state.AppliedAt = _clock.GetUtcNow();
        state.StartupMode = AppCatalogStartupModes.Normal;
        db.AppCatalogAudits.Add(NewAppliedAudit(snapshot, reconciliation));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        runtimeSnapshot?.Enable();
        return AppCatalogStartupResult.Applied;
    }

    private async Task<CatalogReconciliationSummary> ReconcileMappingsAsync(
        AppCatalogSnapshot snapshot,
        CancellationToken cancellationToken,
        bool recordCatalogObservations = true)
    {
        var activeOverrides = await db.AppCatalogOverrides
            .Include(x => x.AppIdentity)
            .Include(x => x.TargetApp)
            .Where(x => x.Status == AppCatalogOverrideStatuses.Active)
            .ToListAsync(cancellationToken);
        var overrideByIdentity = activeOverrides.ToDictionary(x => x.AppIdentity.Key, StringComparer.Ordinal);
        var builtInByIdentity = snapshot.Document.Products
            .SelectMany(product => product.Identities.Select(identity => (identity, product)))
            .ToDictionary(x => x.identity, x => x.product, StringComparer.Ordinal);
        var now = _clock.GetUtcNow();
        var affectedProducts = 0;
        var affectedIdentities = 0;
        var promoted = 0;
        var shadowed = 0;

        // Repair active intent first. This also makes startup self-healing if an older/manual write
        // changed AppIdentity.AppId without changing the durable Override row.
        foreach (var localOverride in activeOverrides.OrderBy(x => x.AppIdentity.Key, StringComparer.Ordinal))
        {
            var target = localOverride.TargetApp
                ?? throw new AppCatalogException(
                    $"Active Override {localOverride.Id} has no target App.");
            var product = new AppCatalogProduct(target.Key, target.DisplayName, [localOverride.AppIdentity.Key]);
            IReadOnlySet<string>? preserveSourceAppKeys = null;
            if (builtInByIdentity.TryGetValue(localOverride.AppIdentity.Key, out var builtInProduct))
                preserveSourceAppKeys = new HashSet<string>([builtInProduct.Key], StringComparer.Ordinal);
            await _products.ReconcileAsync(
                product, [localOverride.AppIdentity.Key], target, markTargetFormal: true,
                preserveSourceAppKeys: preserveSourceAppKeys,
                cancellationToken: cancellationToken);
        }

        foreach (var product in snapshot.Document.Products.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            var effective = new List<string>();
            foreach (var identityKey in product.Identities)
            {
                if (!overrideByIdentity.TryGetValue(identityKey, out var localOverride))
                {
                    effective.Add(identityKey);
                    continue;
                }

                if (string.Equals(localOverride.TargetAppKey, product.Key, StringComparison.Ordinal))
                {
                    localOverride.Status = AppCatalogOverrideStatuses.Promoted;
                    localOverride.UpdatedAt = now;
                    localOverride.PromotedAt = now;
                    localOverride.TargetApp = null;
                    localOverride.TargetAppId = null;
                    promoted++;
                    effective.Add(identityKey);
                    db.AppCatalogAudits.Add(new AppCatalogAudit
                    {
                        EventType = "override-promoted",
                        SchemaVersion = snapshot.Document.SchemaVersion,
                        CatalogVersion = snapshot.Document.CatalogVersion,
                        ContentHash = snapshot.ContentHash,
                        ActorSubject = localOverride.UpdatedBySubject,
                        OccurredAt = now,
                        SummaryJson = JsonSerializer.Serialize(new
                        {
                            identityKey,
                            targetAppKey = product.Key,
                            overrideId = localOverride.Id
                        })
                    });
                    continue;
                }

                shadowed++;
                logger.LogWarning(
                    "App Catalog identity {IdentityKey} maps to {CatalogAppKey}, but active Override {OverrideId} retains target {OverrideAppKey}.",
                    identityKey, product.Key, localOverride.Id, localOverride.TargetAppKey);
                if (recordCatalogObservations) db.AppCatalogAudits.Add(new AppCatalogAudit
                {
                    EventType = "catalog-shadowed",
                    SchemaVersion = snapshot.Document.SchemaVersion,
                    CatalogVersion = snapshot.Document.CatalogVersion,
                    ContentHash = snapshot.ContentHash,
                    ActorSubject = localOverride.UpdatedBySubject,
                    OccurredAt = now,
                    SummaryJson = JsonSerializer.Serialize(new
                    {
                        identityKey,
                        catalogAppKey = product.Key,
                        overrideAppKey = localOverride.TargetAppKey,
                        overrideId = localOverride.Id
                    })
                });
            }

            if (effective.Count == 0) continue;
            var result = await _products.ReconcileAsync(product, effective, cancellationToken: cancellationToken);
            if (result.TargetAppId == 0) continue;
            affectedProducts++;
            affectedIdentities += result.IdentityKeys.Count;
        }

        return new(affectedProducts, affectedIdentities, promoted, shadowed);
    }

    private AppCatalogState NewState(AppCatalogSnapshot snapshot) => new()
    {
        Id = 1,
        SchemaVersion = snapshot.Document.SchemaVersion,
        CatalogVersion = snapshot.Document.CatalogVersion,
        ContentHash = snapshot.ContentHash,
        AppliedAt = _clock.GetUtcNow(),
        StartupMode = AppCatalogStartupModes.Normal
    };

    private AppCatalogAudit NewAppliedAudit(
        AppCatalogSnapshot snapshot,
        CatalogReconciliationSummary summary) => new()
    {
        EventType = "catalog-applied",
        SchemaVersion = snapshot.Document.SchemaVersion,
        CatalogVersion = snapshot.Document.CatalogVersion,
        ContentHash = snapshot.ContentHash,
        OccurredAt = _clock.GetUtcNow(),
        SummaryJson = JsonSerializer.Serialize(new
        {
            products = snapshot.Document.Products.Count,
            affectedProducts = summary.AffectedProducts,
            affectedIdentities = summary.AffectedIdentities,
            promotedOverrides = summary.PromotedOverrides,
            shadowedOverrides = summary.ShadowedOverrides
        })
    };

    private sealed record CatalogReconciliationSummary(
        int AffectedProducts,
        int AffectedIdentities,
        int PromotedOverrides,
        int ShadowedOverrides);
}

public enum AppCatalogStartupResult
{
    Applied,
    AlreadyApplied,
    RollbackCompatible
}
