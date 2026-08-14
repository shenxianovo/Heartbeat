using Heartbeat.Core.DTOs.Apps;
using Heartbeat.Server.AppCatalog;
using Heartbeat.Server.Data;
using Heartbeat.Server.Entities;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Services;

public sealed class AppCatalogAdminQueryService(
    AppDbContext db,
    AppCatalogRuntimeSnapshot runtimeCatalog,
    AppCatalogSnapshot builtInCatalog)
{
    public async Task<AppCatalogAdminInventoryResponse> GetInventoryAsync(
        CancellationToken cancellationToken = default)
    {
        var activeOverrides = await db.AppCatalogOverrides
            .AsNoTracking()
            .Include(x => x.AppIdentity)
            .Where(x => x.Status == AppCatalogOverrideStatuses.Active)
            .OrderBy(x => x.AppIdentity.Key)
            .ToListAsync(cancellationToken);
        var overridesByIdentity = activeOverrides.ToDictionary(x => x.AppIdentityId);

        var apps = await db.Apps
            .AsNoTracking()
            .Include(x => x.Identities)
            .OrderBy(x => x.Key)
            .ToListAsync(cancellationToken);
        var usageRows = await db.ActivitySegments
            .AsNoTracking()
            .Where(x => x.Source == "system" && x.AppIdentityId != null)
            .GroupBy(x => x.AppIdentity!.AppId)
            .Select(group => new
            {
                AppId = group.Key,
                SegmentCount = group.Count(),
                DurationSeconds = (long)group.Sum(row => (row.EndTime - row.StartTime).TotalSeconds),
                DeviceCount = group.Select(row => row.DeviceId).Distinct().Count(),
                LastObservedAt = (DateTimeOffset?)group.Max(row => row.EndTime)
            })
            .ToListAsync(cancellationToken);
        var usageByApp = usageRows.ToDictionary(
            x => x.AppId,
            x => new AppCatalogAdminUsageResponse
            {
                SegmentCount = x.SegmentCount,
                DurationSeconds = x.DurationSeconds,
                DeviceCount = x.DeviceCount,
                LastObservedAt = x.LastObservedAt
            });

        return new AppCatalogAdminInventoryResponse
        {
            SchemaVersion = builtInCatalog.Document.SchemaVersion,
            CatalogVersion = builtInCatalog.Document.CatalogVersion,
            IsRollbackCompatible = runtimeCatalog.IsRollbackCompatible,
            ActiveOverrides = activeOverrides.Select(ToOverride).ToList(),
            Products = apps.Select(app => new AppCatalogAdminProductResponse
            {
                Id = app.Id,
                Key = app.Key,
                DisplayName = app.DisplayName,
                IsProvisional = app.IsProvisional,
                Identities = app.Identities.OrderBy(x => x.Key).Select(identity =>
                {
                    overridesByIdentity.TryGetValue(identity.Id, out var localOverride);
                    return new AppCatalogAdminIdentityResponse
                    {
                        Id = identity.Id,
                        Key = identity.Key,
                        EffectiveSource = EffectiveSource(app, identity.Key, localOverride),
                        ActiveOverride = localOverride is null ? null : ToOverride(localOverride)
                    };
                }).ToList(),
                Usage = usageByApp.GetValueOrDefault(app.Id) ?? new AppCatalogAdminUsageResponse()
            }).ToList()
        };
    }

    public async Task<AppCatalogAdminAuditListResponse> GetAuditAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        var boundedLimit = Math.Clamp(limit, 1, 200);
        return new AppCatalogAdminAuditListResponse
        {
            Entries = await db.AppCatalogAudits.AsNoTracking()
                .OrderByDescending(x => x.OccurredAt)
                .ThenByDescending(x => x.Id)
                .Take(boundedLimit)
                .Select(x => new AppCatalogAdminAuditResponse
                {
                    Id = x.Id,
                    EventType = x.EventType,
                    SchemaVersion = x.SchemaVersion,
                    CatalogVersion = x.CatalogVersion,
                    ContentHash = x.ContentHash,
                    ActorSubject = x.ActorSubject,
                    OccurredAt = x.OccurredAt,
                    SummaryJson = x.SummaryJson
                })
                .ToListAsync(cancellationToken)
        };
    }

    private string EffectiveSource(
        App app,
        string identityKey,
        AppCatalogOverride? localOverride)
    {
        if (localOverride is not null) return "override";
        if (runtimeCatalog.IsRollbackCompatible)
            return app.IsProvisional ? "provisional" : "retained";
        if (runtimeCatalog.TryGetProduct(identityKey, out _)) return "built-in";
        return app.IsProvisional ? "provisional" : "classified";
    }

    private static AppCatalogAdminOverrideResponse ToOverride(AppCatalogOverride value) => new()
    {
        Id = value.Id,
        IdentityKey = value.AppIdentity.Key,
        TargetAppKey = value.TargetAppKey,
        Status = value.Status,
        CreatedBySubject = value.CreatedBySubject,
        UpdatedBySubject = value.UpdatedBySubject,
        CreatedAt = value.CreatedAt,
        UpdatedAt = value.UpdatedAt
    };
}
