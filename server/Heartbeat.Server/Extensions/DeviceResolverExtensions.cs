using Heartbeat.Server.Data;
using Heartbeat.Server.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Extensions
{
    public static class DeviceResolverExtensions
    {
        public const string DeviceNameHeader = "X-Device-Name";

        /// <summary>
        /// Resolve Device from X-Device-Name header.
        /// Returns null if the header is missing or the device is not found.
        /// If autoCreate is true, creates a new Device when not found.
        /// </summary>
        public static async Task<Device?> ResolveDeviceAsync(
            this ControllerBase controller,
            AppDbContext db,
            bool autoCreate = true)
        {
            var rawHeader = controller.Request.Headers[DeviceNameHeader].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(rawHeader))
                return null;

            // Agent URL-encodes the name to support non-ASCII characters
            var deviceName = Uri.UnescapeDataString(rawHeader);

            var device = await db.Devices.FirstOrDefaultAsync(d => d.DeviceName == deviceName);

            if (device == null && autoCreate)
            {
                device = new Device { DeviceName = deviceName };
                db.Devices.Add(device);
                await db.SaveChangesAsync();
            }

            return device;
        }
    }
}
