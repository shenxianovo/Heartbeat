using Heartbeat.Core.DTOs.Devices;
using Heartbeat.Server.Data;
using Heartbeat.Server.Entities;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Services
{
    public class DeviceService(AppDbContext db, AppIdentityService? identityService = null)
    {
        public const string DeviceNameHeader = "X-Device-Name";
        public const string HardwareIdHeader = "X-Hardware-Id";

        private readonly AppDbContext _db = db;
        private readonly AppIdentityService _identityService = identityService ?? new AppIdentityService(db);

        /// <summary>
        /// Agent 通过 HTTP header 传设备名时会用 Uri.EscapeDataString 编码
        /// （header 不能直接携带非 ASCII 字符）。此处解码还原真实设备名。
        /// 对未编码的明文是幂等安全的。
        /// </summary>
        internal static string DecodeDeviceName(string? deviceName)
        {
            if (string.IsNullOrEmpty(deviceName))
                return string.Empty;

            try
            {
                return Uri.UnescapeDataString(deviceName);
            }
            catch (Exception)
            {
                return deviceName;
            }
        }

        public async Task<List<DeviceInfoResponse>> GetAllAsync(string ownerId)
        {
            return await _db.Devices
                .Where(x => x.OwnerId == ownerId)
                .Select(x => new DeviceInfoResponse
                {
                    Id = x.Id,
                    Name = x.DeviceName
                })
                .ToListAsync();
        }

        public async Task<DeviceStatusResponse?> GetStatusAsync(long deviceId, string ownerId)
        {
            return await _db.Devices
                .Where(d => d.Id == deviceId && d.OwnerId == ownerId)
                .Select(d => new DeviceStatusResponse
                {
                    Id = d.Id,
                    CurrentAppId = d.CurrentAppIdentityId != null ? d.CurrentAppIdentity!.AppId : null,
                    CurrentAppKey = d.CurrentAppIdentityId != null ? d.CurrentAppIdentity!.App.Key : null,
                    CurrentAppDisplayName = d.CurrentAppIdentityId != null
                        ? d.CurrentAppIdentity!.App.DisplayName
                        : d.CurrentApp,
                    CurrentAppIdentityKey = d.CurrentAppIdentityId != null ? d.CurrentAppIdentity!.Key : null,
                    CurrentApp = d.CurrentAppIdentityId != null
                        ? d.CurrentAppIdentity!.App.DisplayName
                        : d.CurrentApp,
                    LastSeen = d.LastSeen
                })
                .FirstOrDefaultAsync();
        }

        /// <summary>Fact references retain legacy hardware text while UUID spellings identify one device.</summary>
        internal async Task<Device> ResolveFactReferenceAsync(string ownerId, string reference,
            string? displayName, CancellationToken ct)
        {
            if (Guid.TryParse(reference, out _))
            {
                var devices = await _db.Devices.Where(d => d.OwnerId == ownerId).ToListAsync(ct);
                var existing = devices.SingleOrDefault(d => SameHardwareIdentity(d.HardwareId, reference));
                if (existing is not null) return existing;
            }
            return await ResolveByHardwareIdAsync(ownerId, reference, displayName);
        }

        internal static bool SameHardwareIdentity(string? first, string second) => first == second ||
            Guid.TryParse(first, out var firstId) && Guid.TryParse(second, out var secondId) && firstId == secondId;

        public async Task<Device> ResolveByHardwareIdAsync(string ownerId, string hardwareId, string? deviceName = null)
        {
            var device = await _db.Devices
                .FirstOrDefaultAsync(d => d.OwnerId == ownerId && d.HardwareId == hardwareId);

            if (device == null)
            {
                device = new Device
                {
                    OwnerId = ownerId,
                    HardwareId = hardwareId,
                    DeviceName = DecodeDeviceName(deviceName)
                };
                _db.Devices.Add(device);
                await _db.SaveChangesAsync();
            }

            return device;
        }


        public async Task UpdateStatusAsync(
            Device device,
            string? currentAppIdentityKey,
            string? observedDisplayName)
        {
            AppIdentity? identity = null;
            if (!string.IsNullOrWhiteSpace(currentAppIdentityKey))
                identity = await _identityService.ResolveAsync(currentAppIdentityKey, observedDisplayName);

            device.CurrentAppIdentityId = identity?.Id;
            device.CurrentApp = identity?.App.DisplayName ?? observedDisplayName ?? string.Empty;
            device.LastSeen = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync();
        }
    }
}
