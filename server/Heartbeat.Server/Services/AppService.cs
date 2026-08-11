using Heartbeat.Core;
using Heartbeat.Core.DTOs.Apps;
using Heartbeat.Server.Data;
using Heartbeat.Server.Entities;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Services
{
    public class AppService(AppDbContext db, AppIdentityService? identityService = null)
    {
        private readonly AppDbContext _db = db;
        private readonly AppIdentityService _identityService = identityService ?? new AppIdentityService(db);

        public async Task<List<AppInfoResponse>> GetAppsForUserAsync(string ownerId)
        {
            // 只看 system 段：App 列表 = 该用户前台用过的应用。插件段的 AppId 是关联提示，不定义"用过"。
            return await _db.ActivitySegments
                .Where(u => u.Device.OwnerId == ownerId)
                .Where(u => u.Source == ActivitySources.System)
                .Select(u => new
                {
                    Id = u.AppIdentityId != null ? u.AppIdentity!.AppId : u.AppId!.Value,
                    Key = u.AppIdentityId != null ? u.AppIdentity!.App.Key : u.App!.Key,
                    DisplayName = u.AppIdentityId != null
                        ? u.AppIdentity!.App.DisplayName
                        : u.App!.DisplayName
                })
                .Distinct()
                .Select(a => new AppInfoResponse
                {
                    Id = a.Id,
                    Key = a.Key,
                    DisplayName = a.DisplayName,
                    Name = a.DisplayName
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

        public async Task UploadIconAsync(
            string ownerId,
            string? appIdentityKey,
            string? observedDisplayName,
            byte[] iconData,
            bool refresh = false)
        {
            var identityKey = !string.IsNullOrWhiteSpace(appIdentityKey)
                ? appIdentityKey
                : AppIdentityKeys.FromLegacyWindowsAppName(observedDisplayName!);
            var identity = await _identityService.ResolveAsync(identityKey, observedDisplayName);

            var existing = await _db.AppIcons
                .FirstOrDefaultAsync(x => x.OwnerId == ownerId && x.AppId == identity.AppId);

            if (existing != null)
            {
                if (!refresh) return;
                existing.IconData = iconData;
                existing.UpdatedAt = DateTimeOffset.UtcNow;
            }
            else
            {
                _db.AppIcons.Add(new AppIcon
                {
                    AppId = identity.AppId,
                    OwnerId = ownerId,
                    IconData = iconData,
                    UpdatedAt = DateTimeOffset.UtcNow
                });
            }

            await _db.SaveChangesAsync();
        }
    }
}
