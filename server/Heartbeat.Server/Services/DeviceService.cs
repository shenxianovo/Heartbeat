using Heartbeat.Core.DTOs.Devices;
using Heartbeat.Server.Data;
using Heartbeat.Server.Entities;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Services
{
    public class DeviceService(AppDbContext db)
    {
        public const string DeviceNameHeader = "X-Device-Name";

        private readonly AppDbContext _db = db;

        public async Task<List<DeviceInfoResponse>> GetAllAsync()
        {
            return await _db.Devices
                .Select(x => new DeviceInfoResponse
                {
                    Id = x.Id,
                    Name = x.DeviceName
                })
                .ToListAsync();
        }

        public async Task<DeviceStatusResponse?> GetStatusAsync(long deviceId)
        {
            return await _db.Devices
                .Where(d => d.Id == deviceId)
                .Select(d => new DeviceStatusResponse
                {
                    Id = d.Id,
                    CurrentApp = d.CurrentApp,
                    LastSeen = d.LastSeen
                })
                .FirstOrDefaultAsync();
        }

        public async Task<Device?> ResolveByNameAsync(string? rawHeader, bool autoCreate = true)
        {
            if (string.IsNullOrWhiteSpace(rawHeader))
                return null;

            var deviceName = Uri.UnescapeDataString(rawHeader);

            var device = await _db.Devices.FirstOrDefaultAsync(d => d.DeviceName == deviceName);

            if (device == null && autoCreate)
            {
                device = new Device { DeviceName = deviceName };
                _db.Devices.Add(device);
                await _db.SaveChangesAsync();
            }

            return device;
        }

        public async Task UpdateStatusAsync(Device device, string currentApp)
        {
            device.CurrentApp = currentApp;
            device.LastSeen = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync();
        }
    }
}
