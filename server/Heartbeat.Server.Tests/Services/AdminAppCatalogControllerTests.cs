using Heartbeat.Core;
using Heartbeat.Core.DTOs.Knowledge;
using Heartbeat.Server.AppCatalog;
using Heartbeat.Server.Controllers;
using Heartbeat.Server.Entities;
using Heartbeat.Server.Services;
using Heartbeat.Server.Tests.Fixtures;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Heartbeat.Server.Tests.Services;

[Collection("postgres")]
public sealed class AdminAppCatalogControllerTests(PostgresContainerFixture fixture) : PostgresTestBase(fixture)
{
    [Fact]
    public async Task Inventory_ProjectsEffectiveSourceAndAggregateUsage_WithoutOwnerData()
    {
        await using var db = CreateDbContext();
        var chrome = new App { Key = "chrome", DisplayName = "Google Chrome", IsProvisional = false };
        var unknown = new App { Key = "mystery", DisplayName = "Mystery", IsProvisional = true };
        var chromeIdentity = new AppIdentity { Key = "win:chrome", App = chrome };
        var unknownIdentity = new AppIdentity { Key = "win:mystery", App = unknown };
        var device = new Device
        {
            OwnerId = "private-owner-sub",
            HardwareId = "machine-1",
            DeviceName = "Private Laptop",
            LastSeen = new DateTimeOffset(2026, 8, 14, 8, 0, 0, TimeSpan.Zero)
        };
        db.AddRange(chrome, unknown, chromeIdentity, unknownIdentity, device);
        db.ActivitySegments.Add(new ActivitySegment
        {
            Id = Guid.CreateVersion7(), Device = device, Source = "system",
            IdentityKey = "win:chrome|docs", AppIdentity = chromeIdentity,
            StartTime = new DateTimeOffset(2026, 8, 14, 7, 0, 0, TimeSpan.Zero),
            EndTime = new DateTimeOffset(2026, 8, 14, 7, 5, 0, TimeSpan.Zero),
            Title = "private title"
        });
        await db.SaveChangesAsync();

        var controller = CreateController(db, "admin-sub", CatalogSnapshot());
        var response = await controller.GetInventory(default);

        var inventory = response.Value!;
        var chromeProduct = Assert.Single(inventory.Products, x => x.Key == "chrome");
        var identity = Assert.Single(chromeProduct.Identities);
        Assert.Equal("built-in", identity.EffectiveSource);
        Assert.Equal(1, chromeProduct.Usage.SegmentCount);
        Assert.Equal(300, chromeProduct.Usage.DurationSeconds);
        Assert.Equal(1, chromeProduct.Usage.DeviceCount);
        Assert.Equal(new DateTimeOffset(2026, 8, 14, 7, 5, 0, TimeSpan.Zero), chromeProduct.Usage.LastObservedAt);

        var provisional = Assert.Single(inventory.Products, x => x.Key == "mystery");
        Assert.True(provisional.IsProvisional);
        Assert.Equal("provisional", Assert.Single(provisional.Identities).EffectiveSource);
        var serialized = System.Text.Json.JsonSerializer.Serialize(inventory);
        Assert.DoesNotContain("private-owner-sub", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("Private Laptop", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("private title", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NonAdmin_SetOverride_ReturnsForbidden_WithoutMutationOrAudit()
    {
        await using var db = CreateDbContext();
        var source = new App { Key = "mystery", DisplayName = "Mystery", IsProvisional = true };
        var target = new App { Key = "chrome", DisplayName = "Google Chrome" };
        db.AddRange(source, target, new AppIdentity { Key = "win:mystery", App = source });
        await db.SaveChangesAsync();
        var controller = CreateController(db, "ordinary-sub", CatalogSnapshot());

        var response = await controller.SetOverride(
            "win:mystery",
            new Heartbeat.Core.DTOs.Apps.AppCatalogOverrideSetRequest { TargetAppKey = "chrome" },
            default);

        Assert.IsType<ForbidResult>(response.Result);
        Assert.Empty(await db.AppCatalogOverrides.ToListAsync());
        Assert.Empty(await db.AppCatalogAudits.ToListAsync());
        Assert.Equal(source.Id, (await db.AppIdentities.SingleAsync()).AppId);
    }

    [Fact]
    public async Task PreviewAndSetOverride_ReturnSameImpact_ButOnlySetCommits()
    {
        await using var db = CreateDbContext();
        var source = new App { Key = "mystery", DisplayName = "Mystery", IsProvisional = true };
        var target = new App { Key = "chrome", DisplayName = "Google Chrome" };
        var identity = new AppIdentity { Key = "win:mystery", App = source };
        db.AddRange(source, target, identity, new AppIcon
        {
            OwnerId = "owner", App = source, IconData = [1], UpdatedAt = DateTimeOffset.UtcNow
        }, new MutedMatcher
        {
            Id = Guid.CreateVersion7(), OwnerId = "owner", Source = ActivitySources.System,
            StepsJson = AppMatcher("mystery"), CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db, "admin-sub", CatalogSnapshot());
        var request = new Heartbeat.Core.DTOs.Apps.AppCatalogOverrideSetRequest { TargetAppKey = "chrome" };

        var preview = (await controller.PreviewOverride(identity.Key, request, default)).Value!;
        Assert.Null(preview.TargetAppId);
        db.ChangeTracker.Clear();
        Assert.Empty(await db.AppCatalogOverrides.ToListAsync());
        Assert.Empty(await db.AppCatalogAudits.ToListAsync());
        Assert.True(await db.Apps.AnyAsync(x => x.Id == source.Id));
        Assert.Equal("mystery", preview.RemovedProducts.Single().Key);
        var iconImpact = Assert.Single(preview.IconImpacts);
        Assert.Equal("move-source", iconImpact.Resolution);
        Assert.Equal(1, iconImpact.Count);
        var knowledge = Assert.Single(preview.KnowledgeChanges);
        Assert.Equal("muted-matcher", knowledge.Category);
        Assert.Equal(AppMatcher("mystery"), knowledge.BeforeStepsJson);
        Assert.Equal(AppMatcher("chrome"), knowledge.AfterStepsJson);
        Assert.Equal(AppMatcher("mystery"), (await db.MutedMatchers.SingleAsync()).StepsJson);

        var committed = (await controller.SetOverride(identity.Key, request, default)).Value!;

        Assert.Equal(target.Id, committed.TargetAppId);
        Assert.Equal(preview.TargetAppKey, committed.TargetAppKey);
        Assert.Equal(preview.IdentityKeys, committed.IdentityKeys);
        Assert.Equal(preview.ProductsRemoved, committed.ProductsRemoved);
        Assert.Equal(preview.IconsMovedOrRemoved, committed.IconsMovedOrRemoved);
        Assert.Equal(preview.KnowledgeRowsChangedOrDeduplicated,
            committed.KnowledgeRowsChangedOrDeduplicated);
        Assert.Equal(AppMatcher("chrome"), (await db.MutedMatchers.SingleAsync()).StepsJson);
        Assert.Single(await db.AppCatalogOverrides.ToListAsync());
        Assert.Single(await db.AppCatalogAudits.ToListAsync());
    }

    [Fact]
    public async Task PreviewDelete_RollsBack_AndDeleteUsesSameFallback()
    {
        await using var db = CreateDbContext();
        var chrome = new App { Key = "chrome", DisplayName = "Google Chrome" };
        var privateTarget = new App { Key = "private-browser", DisplayName = "Private Browser" };
        var identity = new AppIdentity { Key = "win:chrome", App = privateTarget };
        db.AddRange(chrome, privateTarget, identity);
        await db.SaveChangesAsync();
        db.AppCatalogOverrides.Add(new AppCatalogOverride
        {
            AppIdentityId = identity.Id,
            TargetAppId = privateTarget.Id,
            TargetAppKey = privateTarget.Key,
            Status = AppCatalogOverrideStatuses.Active,
            CreatedBySubject = "admin-sub",
            UpdatedBySubject = "admin-sub",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db, "admin-sub", CatalogSnapshot());

        var preview = (await controller.PreviewDeleteOverride(identity.Key, default)).Value!;
        db.ChangeTracker.Clear();
        Assert.Equal("catalog", preview.FallbackSource);
        Assert.Equal(AppCatalogOverrideStatuses.Active,
            (await db.AppCatalogOverrides.SingleAsync()).Status);
        Assert.Equal(privateTarget.Id, (await db.AppIdentities.SingleAsync()).AppId);
        Assert.Empty(await db.AppCatalogAudits.ToListAsync());

        var committed = (await controller.DeleteOverride(identity.Key, default)).Value!;
        Assert.Equal(preview.TargetAppKey, committed.TargetAppKey);
        Assert.Equal(preview.FallbackSource, committed.FallbackSource);
        Assert.Equal(AppCatalogOverrideStatuses.Deleted,
            (await db.AppCatalogOverrides.SingleAsync()).Status);
        Assert.Equal(chrome.Id, (await db.AppIdentities.SingleAsync()).AppId);
        Assert.Single(await db.AppCatalogAudits.ToListAsync());
    }

    [Fact]
    public async Task Audit_ReturnsRecentCatalogEvents_WithoutJoiningOwnerData()
    {
        await using var db = CreateDbContext();
        db.AppCatalogAudits.AddRange(
            new AppCatalogAudit
            {
                EventType = "catalog-applied", CatalogVersion = 2,
                OccurredAt = new DateTimeOffset(2026, 8, 14, 7, 0, 0, TimeSpan.Zero),
                SummaryJson = "{\"products\":1}"
            },
            new AppCatalogAudit
            {
                EventType = "override-created", ActorSubject = "admin-sub",
                OccurredAt = new DateTimeOffset(2026, 8, 14, 8, 0, 0, TimeSpan.Zero),
                SummaryJson = "{\"identityKey\":\"win:mystery\"}"
            });
        db.Devices.Add(new Device
        {
            OwnerId = "private-owner-sub", HardwareId = "machine-1",
            DeviceName = "Private Laptop", LastSeen = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db, "admin-sub", CatalogSnapshot());

        var response = (await controller.GetAudit(1, default)).Value!;

        var entry = Assert.Single(response.Entries);
        Assert.Equal("override-created", entry.EventType);
        Assert.Equal("admin-sub", entry.ActorSubject);
        var serialized = System.Text.Json.JsonSerializer.Serialize(response);
        Assert.DoesNotContain("private-owner-sub", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("Private Laptop", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetOverride_ReturnsTypedNotFoundError()
    {
        await using var db = CreateDbContext();
        var source = new App { Key = "mystery", DisplayName = "Mystery", IsProvisional = true };
        db.AddRange(source, new AppIdentity { Key = "win:mystery", App = source });
        await db.SaveChangesAsync();
        var controller = CreateController(db, "admin-sub", CatalogSnapshot());

        var response = await controller.SetOverride(
            "win:mystery",
            new Heartbeat.Core.DTOs.Apps.AppCatalogOverrideSetRequest { TargetAppKey = "missing" },
            default);

        var notFound = Assert.IsType<NotFoundObjectResult>(response.Result);
        var error = Assert.IsType<Heartbeat.Core.DTOs.Apps.AppCatalogAdminErrorResponse>(notFound.Value);
        Assert.Equal("target_not_found", error.Code);
        Assert.Empty(await db.AppCatalogOverrides.ToListAsync());
        Assert.Empty(await db.AppCatalogAudits.ToListAsync());
    }

    [Fact]
    public async Task NonAdmin_Export_ReturnsForbidden()
    {
        await using var db = CreateDbContext();
        var controller = CreateController(db, "ordinary-sub", CatalogSnapshot());

        var response = await controller.Export(
            new Heartbeat.Core.DTOs.Apps.AppCatalogExportRequest
            {
                SelectedIdentityKeys = ["win:chrome"]
            },
            default);

        Assert.IsType<ForbidResult>(response.Result);
        Assert.Empty(await db.AppCatalogAudits.ToListAsync());
        Assert.Empty(await db.AppCatalogStates.ToListAsync());
    }

    private static AdminAppCatalogController CreateController(
        Heartbeat.Server.Data.AppDbContext db,
        string subject,
        AppCatalogSnapshot snapshot)
    {
        var runtime = new AppCatalogRuntimeSnapshot(snapshot);
        runtime.Enable();
        return new AdminAppCatalogController(
            new AppCatalogAdminQueryService(db, runtime, snapshot),
            new AppCatalogOverrideService(db, new AppProductReconciliationService(db), runtime),
            new AppCatalogExportService(db, snapshot, runtime),
            new AdminAuthorizationService(Options.Create(new AdministrationOptions { Subjects = ["admin-sub"] })),
            new FakeCurrentUser(subject));
    }

    private static AppCatalogSnapshot CatalogSnapshot()
        => AppCatalogLoader.Parse(
            """
            {
              "schemaVersion": 1,
              "catalogVersion": 2,
              "products": [
                {
                  "key": "chrome",
                  "displayName": "Google Chrome",
                  "identities": ["win:chrome"]
                }
              ]
            }
            """);

    private static string AppMatcher(string value) => MatcherCodec.Serialize(
        [new MatcherStepDto { Reading = "app", Op = MatcherOps.Equal, Value = value }]);

    private sealed class FakeCurrentUser(string subject) : ICurrentUserService
    {
        public string GetUserId() => subject;
        public string? GetUserIdOrNull() => subject;
        public string? GetUsernameOrNull() => null;
    }
}
