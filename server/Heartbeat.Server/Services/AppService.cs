using Heartbeat.Core.DTOs.Apps;
using Heartbeat.Server.Data;
using Heartbeat.Server.Entities;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Services
{
    public class AppService(AppDbContext db)
    {
        private readonly AppDbContext _db = db;

        public async Task<List<AppInfoResponse>> GetAllAsync()
        {
            return await _db.Apps
                .AsNoTracking()
                .Select(a => new AppInfoResponse
                {
                    Id = a.Id,
                    Name = a.Name
                })
                .ToListAsync();
        }

        public async Task<List<AppInfoResponse>> GetAppsForUserAsync(string ownerId)
        {
            return await _db.AppUsages
                .Where(u => u.Device.OwnerId == ownerId)
                .Select(u => u.App)
                .Distinct()
                .Select(a => new AppInfoResponse
                {
                    Id = a.Id,
                    Name = a.Name
                })
                .ToListAsync();
        }

        public async Task<byte[]?> GetIconAsync(long appId)
        {
            var icon = await _db.AppIcons
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.AppId == appId);

            return icon?.IconData;
        }

        public async Task UploadIconAsync(string appName, byte[] iconData)
        {
            var app = await _db.Apps.FirstOrDefaultAsync(a => a.Name == appName);
            if (app == null)
            {
                app = new App { Name = appName };
                _db.Apps.Add(app);
                await _db.SaveChangesAsync();
            }

            var existing = await _db.AppIcons
                .FirstOrDefaultAsync(x => x.AppId == app.Id);

            if (existing != null)
            {
                existing.IconData = iconData;
                existing.UpdatedAt = DateTimeOffset.UtcNow;
            }
            else
            {
                _db.AppIcons.Add(new AppIcon
                {
                    AppId = app.Id,
                    IconData = iconData,
                    UpdatedAt = DateTimeOffset.UtcNow
                });
            }

            await _db.SaveChangesAsync();
        }
    }
}
