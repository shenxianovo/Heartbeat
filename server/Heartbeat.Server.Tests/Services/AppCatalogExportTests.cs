using System.Security.Cryptography;
using System.Text;
using Heartbeat.Core.DTOs.Apps;
using Heartbeat.Server.AppCatalog;
using Heartbeat.Server.Entities;
using Heartbeat.Server.Services;
using Heartbeat.Server.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Tests.Services;

[Collection("postgres")]
public sealed class AppCatalogExportTests(PostgresContainerFixture fixture) : PostgresTestBase(fixture)
{
    [Fact]
    public async Task Export_SelectedOverride_ReturnsExactDeterministicNextVersion_WithoutMutation()
    {
        await using var db = CreateDbContext();
        var snapshot = Snapshot();
        var chrome = new App { Key = "chrome", DisplayName = "Google Chrome" };
        var identity = new AppIdentity { Key = "mac:com.google.chrome", App = chrome };
        var secondIdentity = new AppIdentity { Key = "win:google-chrome", App = chrome };
        db.AddRange(chrome, identity, secondIdentity);
        await db.SaveChangesAsync();
        db.AppCatalogOverrides.AddRange(
            Override(identity),
            Override(secondIdentity));
        await db.SaveChangesAsync();
        var beforeOverrides = await db.AppCatalogOverrides.AsNoTracking().OrderBy(x => x.Id).ToListAsync();

        var first = await new AppCatalogExportService(db, snapshot).ExportAsync(new AppCatalogExportRequest
        {
            SelectedIdentityKeys = [identity.Key, secondIdentity.Key]
        });
        var second = await new AppCatalogExportService(db, snapshot).ExportAsync(new AppCatalogExportRequest
        {
            SelectedIdentityKeys = [secondIdentity.Key, identity.Key, identity.Key]
        });

        const string expected = """
            {
              "schemaVersion": 1,
              "catalogVersion": 3,
              "products": [
                {
                  "key": "chrome",
                  "displayName": "Google Chrome",
                  "identities": [
                    "mac:com.google.chrome",
                    "win:chrome",
                    "win:google-chrome"
                  ]
                },
                {
                  "key": "finder",
                  "displayName": "Finder",
                  "identities": [
                    "mac:com.apple.finder"
                  ]
                }
              ]
            }

            """;
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        Assert.True(first.HasChanges);
        Assert.Equal(3, first.ProposedCatalogVersion);
        Assert.Equal("app-catalog.v3.candidate.json", first.FileName);
        Assert.Equal(expectedBytes, first.Content);
        Assert.Equal(first.Content, second.Content);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(expectedBytes)).ToLowerInvariant(), first.ContentHash);
        var payload = Encoding.UTF8.GetString(first.Content!);
        Assert.DoesNotContain("private-admin-sub", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("owner", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("updatedAt", payload, StringComparison.Ordinal);

        db.ChangeTracker.Clear();
        var afterOverrides = await db.AppCatalogOverrides.AsNoTracking().OrderBy(x => x.Id).ToListAsync();
        Assert.Equal(beforeOverrides.Select(x => x.Status), afterOverrides.Select(x => x.Status));
        Assert.Equal(beforeOverrides.Select(x => x.TargetAppId), afterOverrides.Select(x => x.TargetAppId));
        Assert.Empty(await db.AppCatalogAudits.ToListAsync());
        Assert.Empty(await db.AppCatalogStates.ToListAsync());
    }

    [Fact]
    public async Task Export_EmptyOrAlreadyFormalSelection_ReturnsNoChangeWithoutBytes()
    {
        await using var db = CreateDbContext();
        var snapshot = Snapshot();
        var chrome = new App { Key = "chrome", DisplayName = "Google Chrome" };
        var identity = new AppIdentity { Key = "win:chrome", App = chrome };
        db.AddRange(chrome, identity);
        await db.SaveChangesAsync();
        db.AppCatalogOverrides.Add(new AppCatalogOverride
        {
            AppIdentityId = identity.Id,
            TargetAppId = chrome.Id,
            TargetAppKey = chrome.Key,
            Status = AppCatalogOverrideStatuses.Active,
            CreatedBySubject = "admin-sub",
            UpdatedBySubject = "admin-sub",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        var service = new AppCatalogExportService(db, snapshot);

        var empty = await service.ExportAsync(new AppCatalogExportRequest());
        var equivalent = await service.ExportAsync(new AppCatalogExportRequest
        {
            SelectedIdentityKeys = [identity.Key]
        });

        Assert.False(empty.HasChanges);
        Assert.False(equivalent.HasChanges);
        Assert.Equal(3, equivalent.ProposedCatalogVersion);
        Assert.Null(equivalent.FileName);
        Assert.Null(equivalent.ContentHash);
        Assert.Null(equivalent.Content);
    }

    private static AppCatalogSnapshot Snapshot() => AppCatalogLoader.Parse(
        """
        {
          "schemaVersion": 1,
          "catalogVersion": 2,
          "products": [
            {
              "key": "chrome",
              "displayName": "Google Chrome",
              "identities": ["win:chrome"]
            },
            {
              "key": "finder",
              "displayName": "Finder",
              "identities": ["mac:com.apple.finder"]
            }
          ]
        }
        """);

    private static AppCatalogOverride Override(AppIdentity identity) => new()
    {
        AppIdentityId = identity.Id,
        TargetAppId = identity.AppId,
        TargetAppKey = identity.App.Key,
        Status = AppCatalogOverrideStatuses.Active,
        CreatedBySubject = "private-admin-sub",
        UpdatedBySubject = "private-admin-sub",
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };
}
