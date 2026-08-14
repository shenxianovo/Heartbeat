using Heartbeat.Server.AppCatalog;
using Heartbeat.Server.Data;
using Heartbeat.Server.Entities;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Services;

public sealed class AppCatalogStartupService(
    AppDbContext db,
    ILogger<AppCatalogStartupService> logger,
    TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<AppCatalogStartupResult> ApplyAsync(
        AppCatalogSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var state = await db.AppCatalogStates.SingleOrDefaultAsync(cancellationToken);

        if (state is null)
        {
            state = NewState(snapshot);
            db.AppCatalogStates.Add(state);
            db.AppCatalogAudits.Add(NewAppliedAudit(snapshot));
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
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

            if (state.StartupMode != AppCatalogStartupModes.Normal)
            {
                state.StartupMode = AppCatalogStartupModes.Normal;
                await db.SaveChangesAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            return AppCatalogStartupResult.AlreadyApplied;
        }

        state.SchemaVersion = snapshot.Document.SchemaVersion;
        state.CatalogVersion = snapshot.Document.CatalogVersion;
        state.ContentHash = snapshot.ContentHash;
        state.AppliedAt = _clock.GetUtcNow();
        state.StartupMode = AppCatalogStartupModes.Normal;
        db.AppCatalogAudits.Add(NewAppliedAudit(snapshot));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return AppCatalogStartupResult.Applied;
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

    private AppCatalogAudit NewAppliedAudit(AppCatalogSnapshot snapshot) => new()
    {
        EventType = "catalog-applied",
        SchemaVersion = snapshot.Document.SchemaVersion,
        CatalogVersion = snapshot.Document.CatalogVersion,
        ContentHash = snapshot.ContentHash,
        OccurredAt = _clock.GetUtcNow(),
        SummaryJson = "{\"products\":" + snapshot.Document.Products.Count + "}"
    };
}

public enum AppCatalogStartupResult
{
    Applied,
    AlreadyApplied,
    RollbackCompatible
}
