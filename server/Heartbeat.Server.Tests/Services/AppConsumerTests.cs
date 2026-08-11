using Heartbeat.Core.DTOs.Apps;
using Heartbeat.Server.Entities;
using Heartbeat.Server.Services;
using Heartbeat.Server.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Tests.Services;

[Collection("postgres")]
public class AppConsumerTests(PostgresContainerFixture fixture) : PostgresTestBase(fixture)
{
    [Fact]
    public async Task Presence_StoresRawIdentity_AndProjectsProduct()
    {
        using var db = CreateDbContext();
        var device = new Device { OwnerId = "owner", HardwareId = "hw", DeviceName = "PC" };
        db.Devices.Add(device);
        await db.SaveChangesAsync();

        var service = new DeviceService(db);
        await service.UpdateStatusAsync(device, "win:code", "Code.exe");
        var status = await service.GetStatusAsync(device.Id, "owner");

        Assert.NotNull(device.CurrentAppIdentityId);
        Assert.Equal("win:code", status!.CurrentAppIdentityKey);
        Assert.Equal("code", status.CurrentAppKey);
        Assert.Equal("Code.exe", status.CurrentAppDisplayName);
        Assert.NotNull(status.CurrentAppId);
    }

    [Fact]
    public async Task IconUpload_ResolvesIdentity_FirstValidWins_UntilExplicitRefresh()
    {
        using var db = CreateDbContext();
        var service = new AppService(db);

        await service.UploadIconAsync("owner", "win:code", "Code.exe", [1, 2]);
        await service.UploadIconAsync("owner", "win:code", "Code.exe", [3, 4]);
        var appId = await db.AppIdentities.Where(x => x.Key == "win:code").Select(x => x.AppId).SingleAsync();
        Assert.Equal([1, 2], await service.GetIconAsync("owner", appId));

        await service.UploadIconAsync("owner", "win:code", "Code.exe", [3, 4], refresh: true);
        Assert.Equal([3, 4], await service.GetIconAsync("owner", appId));
        Assert.Single(await db.AppIcons.ToListAsync());
    }

    [Fact]
    public async Task AppList_ExposesStableKeyAndDisplayName()
    {
        using var db = CreateDbContext();
        var app = new App { Key = "vscode", DisplayName = "Visual Studio Code" };
        var identity = new AppIdentity { Key = "win:code", App = app };
        var device = new Device { OwnerId = "owner", HardwareId = "hw", DeviceName = "PC" };
        db.AddRange(app, identity, device);
        await db.SaveChangesAsync();
        db.ActivitySegments.Add(new ActivitySegment
        {
            Id = Guid.CreateVersion7(), DeviceId = device.Id, Source = "system", IdentityKey = "code|",
            AppId = app.Id, AppIdentityId = identity.Id,
            StartTime = DateTimeOffset.UtcNow.AddMinutes(-1), EndTime = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        AppInfoResponse result = Assert.Single(await new AppService(db).GetAppsForUserAsync("owner"));
        Assert.Equal("vscode", result.Key);
        Assert.Equal("Visual Studio Code", result.DisplayName);
    }
}
