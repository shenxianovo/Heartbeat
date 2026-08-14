using Heartbeat.Core.DTOs.Users;
using Heartbeat.Server.Controllers;
using Heartbeat.Server.Services;
using Heartbeat.Server.Tests.Fixtures;
using Microsoft.Extensions.Options;

namespace Heartbeat.Server.Tests.Services;

[Collection("postgres")]
public class MeControllerTests(PostgresContainerFixture fixture) : PostgresTestBase(fixture)
{
    [Fact]
    public async Task Get_ReturnsAdminState_FromConfiguredJwtSubject()
    {
        using var db = CreateDbContext();
        var controller = CreateController(db, subject: "admin-sub", username: "alice", "admin-sub");

        var response = await controller.Get();

        Assert.True(response.Value!.IsAdmin);
    }

    [Fact]
    public async Task UpdateSettings_ReturnsAdminState_FromConfiguredJwtSubject()
    {
        using var db = CreateDbContext();
        await new UserService(db).ProvisionAsync("admin-sub", "renamed-admin");
        var controller = CreateController(db, subject: "admin-sub", username: "renamed-admin", "admin-sub");

        var response = await controller.UpdateSettings(new UpdateMySettingsRequest { IsPublic = true });

        Assert.True(response.Value!.IsAdmin);
    }

    [Fact]
    public async Task Get_DoesNotAuthorizeByUsername()
    {
        using var db = CreateDbContext();
        var controller = CreateController(
            db, subject: "ordinary-sub", username: "admin-sub", "admin-sub");

        var response = await controller.Get();

        Assert.False(response.Value!.IsAdmin);
    }

    [Fact]
    public async Task UpdateSettings_DoesNotAuthorizeByUsername()
    {
        using var db = CreateDbContext();
        await new UserService(db).ProvisionAsync("ordinary-sub", "admin-sub");
        var controller = CreateController(
            db, subject: "ordinary-sub", username: "admin-sub", "admin-sub");

        var response = await controller.UpdateSettings(new UpdateMySettingsRequest { IsPublic = true });

        Assert.False(response.Value!.IsAdmin);
    }

    private static MeController CreateController(
        Heartbeat.Server.Data.AppDbContext db,
        string subject,
        string username,
        params string[] adminSubjects)
    {
        var currentUser = new FakeCurrentUser(subject, username);
        var authorization = new AdminAuthorizationService(Options.Create(new AdministrationOptions
        {
            Subjects = [.. adminSubjects]
        }));
        return new MeController(new UserService(db), currentUser, authorization);
    }

    private sealed class FakeCurrentUser(string subject, string? username) : ICurrentUserService
    {
        public string GetUserId() => subject;
        public string? GetUserIdOrNull() => subject;
        public string? GetUsernameOrNull() => username;
    }
}
