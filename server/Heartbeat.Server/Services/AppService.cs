using Heartbeat.Core;
using Heartbeat.Core.DTOs.Apps;
using Heartbeat.Server.Data;
using Heartbeat.Server.Entities;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Services
{
    public class AppService(AppDbContext db)
    {
        private readonly AppDbContext _db = db;

        public async Task<List<AppInfoResponse>> GetAppsForUserAsync(string ownerId)
        {
            // 只看 system 段：App 列表 = 该用户前台用过的应用。插件段的 AppId 是关联提示，不定义"用过"。
            return await _db.ActivitySegments
                .Where(u => u.Device.OwnerId == ownerId)
                .Where(u => u.Source == ActivitySources.System)
                .Select(u => u.App!)
                .Distinct()
                .Select(a => new AppInfoResponse
                {
                    Id = a.Id,
                    Name = a.Name
                })
                .ToListAsync();
        }

        public async Task<byte[]?> GetIconAsync(string ownerId, long appId)
        {
            var icon = await _db.AppIcons
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.OwnerId == ownerId && x.AppId == appId);

            return icon?.IconData;
        }

        public async Task UploadIconAsync(string ownerId, string appName, byte[] iconData)
        {
            var app = await _db.Apps.FirstOrDefaultAsync(a => a.Name == appName);
            if (app == null)
            {
                app = new App { Name = appName };
                _db.Apps.Add(app);
                await _db.SaveChangesAsync();
            }

            var existing = await _db.AppIcons
                .FirstOrDefaultAsync(x => x.OwnerId == ownerId && x.AppId == app.Id);

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
                    OwnerId = ownerId,
                    IconData = iconData,
                    UpdatedAt = DateTimeOffset.UtcNow
                });
            }

            await _db.SaveChangesAsync();
        }
    }
}
