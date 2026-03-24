using Heartbeat.Core;
using Heartbeat.Core.DTOs;
using Heartbeat.Core.DTOs.Apps;
using Heartbeat.Server.Data;
using Heartbeat.Server.Entities;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Services
{
    public class UsageService(AppDbContext db)
    {
        private readonly AppDbContext _db = db;

        /// <summary>
        /// 时间校验容差：客户端时间与服务端时间偏差不得超过此值
        /// </summary>
        private static readonly TimeSpan TimeSkewTolerance = TimeSpan.FromMinutes(10);

        /// <summary>
        /// 单条记录最大时长限制
        /// </summary>
        private static readonly TimeSpan MaxDuration = TimeSpan.FromHours(24);

        public async Task SaveUsageAsync(long deviceId, UsageUploadRequest request)
        {
            var now = DateTimeOffset.UtcNow;

            var validUsages = request.Usages
                .Where(u => !string.IsNullOrEmpty(u.AppName)
                         && u.StartTime != default
                         && u.EndTime > u.StartTime
                         && u.StartTime.Year >= 2020
                         && u.EndTime <= now + TimeSkewTolerance
                         && u.StartTime >= now - TimeSkewTolerance - MaxDuration
                         && (u.EndTime - u.StartTime) <= MaxDuration)
                .OrderBy(u => u.StartTime)
                .ToList();

            // 获取或创建 App 记录
            var appNames = validUsages.Select(u => u.AppName).Distinct().ToList();
            var existingApps = await _db.Apps
                .Where(a => appNames.Contains(a.Name))
                .ToDictionaryAsync(a => a.Name);

            foreach (var name in appNames)
            {
                if (!existingApps.ContainsKey(name))
                {
                    var app = new App { Name = name };
                    _db.Apps.Add(app);
                    existingApps[name] = app;
                }
            }
            await _db.SaveChangesAsync(); // 保存以获取新 App 的 Id

            if (validUsages.Count == 0) return;

            var first = validUsages[0];
            var firstAppId = existingApps[first.AppName].Id;
            var firstMerged = false;

            // 查该设备+同应用的最新记录，利用 (DeviceId, AppId, EndTime) 索引
            var lastRecord = await _db.AppUsages
                .Where(x => x.DeviceId == deviceId && x.AppId == firstAppId)
                .OrderByDescending(x => x.EndTime)
                .FirstOrDefaultAsync();

            if (lastRecord != null
                && first.StartTime >= lastRecord.EndTime
                && first.StartTime <= lastRecord.EndTime + UsageMerger.MergeTolerance)
            {
                // 批次首条与数据库最新记录同应用且首尾相连 → 上传截断，合并
                if (first.EndTime > lastRecord.EndTime)
                {
                    lastRecord.EndTime = first.EndTime;
                    lastRecord.DurationSeconds = (int)(lastRecord.EndTime - lastRecord.StartTime).TotalSeconds;
                }
                firstMerged = true;
            }

            // 其余记录直接插入
            foreach (var u in validUsages.Skip(firstMerged ? 1 : 0))
            {
                var appId = existingApps[u.AppName].Id;
                _db.AppUsages.Add(new AppUsage
                {
                    DeviceId = deviceId,
                    AppId = appId,
                    StartTime = u.StartTime,
                    EndTime = u.EndTime,
                    DurationSeconds = (int)(u.EndTime - u.StartTime).TotalSeconds
                });
            }

            await _db.SaveChangesAsync();
        }

        public async Task<List<AppUsageResponse>> GetUsageAsync(long? deviceId, DateTimeOffset? start, DateTimeOffset? end)
        {
            var query = _db.AppUsages
                .Include(x => x.App)
                .AsQueryable();

            if (deviceId.HasValue)
                query = query.Where(x => x.DeviceId == deviceId.Value);

            if (start.HasValue)
                query = query.Where(x => x.StartTime >= start.Value);

            if (end.HasValue)
                query = query.Where(x => x.StartTime < end.Value);

            return await query
                .OrderByDescending(x => x.StartTime)
                .Select(x => new AppUsageResponse
                {
                    Id = x.Id,
                    AppId = x.AppId,
                    AppName = x.App.Name,
                    StartTime = x.StartTime,
                    EndTime = x.EndTime,
                    DurationSeconds = x.DurationSeconds
                })
                .ToListAsync();
        }
    }
}
