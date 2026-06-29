using Heartbeat.Server.Services;
using Heartbeat.Server.Tests.Fixtures;

namespace Heartbeat.Server.Tests.Services
{
    [Collection("postgres")]
    public class DeviceServiceTests(PostgresContainerFixture fixture) : PostgresTestBase(fixture)
    {
        [Fact]
        public async Task ResolveByHardwareId_CreatesNewDevice_WhenNotExists()
        {
            using var db = CreateDbContext();
            var service = new DeviceService(db);

            var device = await service.ResolveByHardwareIdAsync("user-1", "hw-abc", "My PC");

            Assert.NotNull(device);
            Assert.Equal("user-1", device.OwnerId);
            Assert.Equal("hw-abc", device.HardwareId);
            Assert.Equal("My PC", device.DeviceName);
        }

        [Fact]
        public async Task ResolveByHardwareId_ReturnsSameDevice_WhenAlreadyExists()
        {
            using var db = CreateDbContext();
            var service = new DeviceService(db);

            var first = await service.ResolveByHardwareIdAsync("user-1", "hw-abc", "My PC");
            var second = await service.ResolveByHardwareIdAsync("user-1", "hw-abc", "My PC");

            Assert.Equal(first!.Id, second!.Id);
        }

        [Fact]
        public async Task ResolveByHardwareId_DifferentUsers_SameHardwareId_CreatesSeparateDevices()
        {
            using var db = CreateDbContext();
            var service = new DeviceService(db);

            var deviceA = await service.ResolveByHardwareIdAsync("user-1", "hw-abc", "PC");
            var deviceB = await service.ResolveByHardwareIdAsync("user-2", "hw-abc", "PC");

            Assert.NotEqual(deviceA!.Id, deviceB!.Id);
            Assert.Equal("user-1", deviceA.OwnerId);
            Assert.Equal("user-2", deviceB.OwnerId);
        }

        [Fact]
        public async Task GetAllAsync_ReturnsOnlyDevicesForOwner()
        {
            using var db = CreateDbContext();
            var service = new DeviceService(db);

            await service.ResolveByHardwareIdAsync("user-1", "hw-1", "PC1");
            await service.ResolveByHardwareIdAsync("user-1", "hw-2", "PC2");
            await service.ResolveByHardwareIdAsync("user-2", "hw-3", "PC3");

            var result = await service.GetAllAsync("user-1");

            Assert.Equal(2, result.Count);
            Assert.All(result, d => Assert.True(d.Name == "PC1" || d.Name == "PC2"));
        }
    }
}
