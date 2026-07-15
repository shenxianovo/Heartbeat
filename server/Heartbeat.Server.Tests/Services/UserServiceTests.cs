using Heartbeat.Server.Services;
using Heartbeat.Server.Tests.Fixtures;

namespace Heartbeat.Server.Tests.Services
{
    [Collection("postgres")]
    public class UserServiceTests(PostgresContainerFixture fixture) : PostgresTestBase(fixture)
    {
        [Fact]
        public async Task ResolveByUsername_ReturnsNull_AndDoesNotCreateRow_WhenUnknown()
        {
            using var db = CreateDbContext();
            var service = new UserService(db);

            var user = await service.ResolveByUsernameAsync("nobody");

            Assert.Null(user);
            Assert.Empty(db.Users);
        }

        [Fact]
        public async Task Provision_CreatesPrivateUser_WhenNotExists()
        {
            using var db = CreateDbContext();
            var service = new UserService(db);

            var user = await service.ProvisionAsync("sub-1", "alice");

            Assert.Equal("sub-1", user.Id);
            Assert.Equal("alice", user.Username);
            Assert.False(user.IsPublic);
        }

        [Fact]
        public async Task Provision_LocatesBySub_AndRefreshesUsername()
        {
            using var db = CreateDbContext();
            var service = new UserService(db);

            await service.ProvisionAsync("sub-1", "alice");
            var renamed = await service.ProvisionAsync("sub-1", "alice2");

            Assert.Equal("sub-1", renamed.Id);
            Assert.Equal("alice2", renamed.Username);
            Assert.Single(db.Users);
        }

        [Fact]
        public async Task Provision_PreservesVisibility_OnRepeatLogin()
        {
            using var db = CreateDbContext();
            var service = new UserService(db);

            await service.ProvisionAsync("sub-1", "alice");
            await service.UpdateVisibilityAsync("sub-1", isPublic: true);
            var again = await service.ProvisionAsync("sub-1", "alice");

            Assert.True(again.IsPublic);
        }

        [Fact]
        public async Task UpdateVisibility_ReturnsNull_WhenUserNotProvisioned()
        {
            using var db = CreateDbContext();
            var service = new UserService(db);

            var result = await service.UpdateVisibilityAsync("ghost", isPublic: true);

            Assert.Null(result);
        }

        [Fact]
        public async Task ResolveByUsername_FindsProvisionedUser()
        {
            using var db = CreateDbContext();
            var service = new UserService(db);

            await service.ProvisionAsync("sub-1", "alice");
            var user = await service.ResolveByUsernameAsync("alice");

            Assert.NotNull(user);
            Assert.Equal("sub-1", user!.Id);
        }
    }
}
