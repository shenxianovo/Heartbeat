using Heartbeat.Core;
using Heartbeat.Core.DTOs.Apps;
using Heartbeat.Core.DTOs.Segments;
using Heartbeat.Core.DTOs.Usage;
using Heartbeat.Server.Data;
using Heartbeat.Server.Entities;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Services
{
    public class UsageService(AppDbContext db)
    {
        private readonly AppDbContext _db = db;

        /// <summary>
        /// 系统采集器路径（/usage）：老判据校验后映射为 system source 段，委托统一摄入例程。
        /// </summary>
        public async Task SaveUsageAsync(long deviceId, UsageUploadRequest request)
        {
            var validUsages = UsageValidationPolicy.Filter(request.Usages, DateTimeOffset.UtcNow);

            if (validUsages.Count == 0) return;

            var items = validUsages.Select(u => new ActivitySegmentItem
            {
                // 旧版 Agent 无 Id（Guid.Empty）→ 服务端代为生成，幂等性退化为 CanMerge 续接。
                Id = u.Id != Guid.Empty ? u.Id : Guid.CreateVersion7(),
                Source = ActivitySources.System,
                IdentityKey = UsageMerger.SystemIdentityKey(u.AppName, u.Title),
                AppName = u.AppName,
                Title = u.Title,
                StartTime = u.StartTime,
                EndTime = u.EndTime
            }).ToList();

            await SaveSegmentsAsync(deviceId, items);
        }

        /// <summary>
        /// 统一摄入例程（ADR-017）：校验 → 幂等去重 → App 关联 → 跨批次续接 → 插入。
        /// 所有 source 共用；'system' 的拒收由公开接缝（SegmentController）负责，此处 source 无关。
        /// </summary>
        public async Task SaveSegmentsAsync(long deviceId, List<ActivitySegmentItem> segments)
        {
            var valid = SegmentValidationPolicy.Filter(segments, DateTimeOffset.UtcNow);
            if (valid.Count == 0) return;

            // 批内重复 Id 只留首条（PK 冲突会让整批反复重试失败）。
            var seenIds = new HashSet<Guid>();
            valid = valid.Where(s => seenIds.Add(s.Id)).ToList();

            // 幂等：过滤掉库中已存在的段 Id（离线重传整批不重复插入，InputEvent 先例）。
            var ids = valid.Select(s => s.Id).ToList();
            var existingIds = await _db.ActivitySegments
                .Where(s => ids.Contains(s.Id))
                .Select(s => s.Id)
                .ToHashSetAsync();
            if (existingIds.Count > 0)
                valid = valid.Where(s => !existingIds.Contains(s.Id)).ToList();
            if (valid.Count == 0) return;

            // AppName 关联提示 → 获取或创建 App 记录
            var appNames = valid
                .Where(s => !string.IsNullOrWhiteSpace(s.AppName))
                .Select(s => s.AppName!)
                .Distinct()
                .ToList();
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
            if (existingApps.Count > 0)
                await _db.SaveChangesAsync(); // 保存以获取新 App 的 Id

            var first = valid[0];
            var firstMerged = false;

            // 查该设备+同活动的最新记录，利用 (DeviceId, Source, IdentityKey, EndTime) 索引
            var lastRecord = await _db.ActivitySegments
                .Where(x => x.DeviceId == deviceId
                    && x.Source == first.Source
                    && x.IdentityKey == first.IdentityKey)
                .OrderByDescending(x => x.EndTime)
                .FirstOrDefaultAsync();

            // 续接判据与客户端共用同一真源（同 Source + 同 IdentityKey + 时间相连）。详见 ADR-017。
            if (lastRecord != null
                && UsageMerger.CanMerge(
                    lastRecord.Source, lastRecord.IdentityKey, lastRecord.EndTime,
                    first.Source, first.IdentityKey, first.StartTime)
                && first.EndTime >= lastRecord.StartTime)
            {
                // 批次首条与数据库最新记录同活动且重叠或首尾相连 → 合并
                if (first.StartTime < lastRecord.StartTime)
                    lastRecord.StartTime = first.StartTime;
                if (first.EndTime > lastRecord.EndTime)
                    lastRecord.EndTime = first.EndTime;
                lastRecord.DurationSeconds = (int)(lastRecord.EndTime - lastRecord.StartTime).TotalSeconds;
                // 续接时保留最新 attributes（易变字段不参与判据，ADR-017 §3a）
                if (first.Attributes.HasValue)
                    lastRecord.Attributes = first.Attributes.Value.GetRawText();
                firstMerged = true;
            }

            // 其余记录直接插入
            foreach (var s in valid.Skip(firstMerged ? 1 : 0))
            {
                _db.ActivitySegments.Add(new ActivitySegment
                {
                    Id = s.Id,
                    DeviceId = deviceId,
                    Source = s.Source,
                    IdentityKey = s.IdentityKey,
                    AppId = !string.IsNullOrWhiteSpace(s.AppName) ? existingApps[s.AppName!].Id : null,
                    Title = s.Title,
                    StartTime = s.StartTime,
                    EndTime = s.EndTime,
                    DurationSeconds = (int)(s.EndTime - s.StartTime).TotalSeconds,
                    Attributes = s.Attributes?.GetRawText()
                });
            }

            await _db.SaveChangesAsync();
        }

        /// <summary>
        /// 插件段查询（ADR-017 §4）：回放多轨用。默认返回全部非 system source
        /// （system 轨走 GetUsageAsync，两者互补不重叠）；source 指定时只查该轨。
        /// </summary>
        public async Task<List<SegmentResponse>> GetSegmentsAsync(
            string ownerId, long? deviceId, string? source, long? appId,
            DateTimeOffset? start, DateTimeOffset? end)
        {
            var query = _db.ActivitySegments
                .Include(x => x.App)
                .Where(x => x.Device.OwnerId == ownerId)
                .AsQueryable();

            query = string.IsNullOrWhiteSpace(source)
                ? query.Where(x => x.Source != ActivitySources.System)
                : query.Where(x => x.Source == source);

            if (deviceId.HasValue)
                query = query.Where(x => x.DeviceId == deviceId.Value);

            if (appId.HasValue)
                query = query.Where(x => x.AppId == appId.Value);

            if (start.HasValue)
                query = query.Where(x => x.StartTime >= start.Value);

            if (end.HasValue)
                query = query.Where(x => x.StartTime < end.Value);

            return await query
                .OrderByDescending(x => x.StartTime)
                .Take(10000)
                .Select(x => new SegmentResponse
                {
                    Id = x.Id,
                    Source = x.Source,
                    IdentityKey = x.IdentityKey,
                    AppId = x.AppId,
                    AppName = x.App != null ? x.App.Name : null,
                    Title = x.Title,
                    StartTime = x.StartTime,
                    EndTime = x.EndTime,
                    DurationSeconds = x.DurationSeconds,
                    Attributes = x.Attributes
                })
                .ToListAsync();
        }

        public async Task<List<AppUsageResponse>> GetUsageAsync(string ownerId, long? deviceId, DateTimeOffset? start, DateTimeOffset? end)
        {
            var query = _db.ActivitySegments
                .Include(x => x.App)
                .Where(x => x.Device.OwnerId == ownerId)
                .Where(x => x.Source == ActivitySources.System)
                .AsQueryable();

            if (deviceId.HasValue)
                query = query.Where(x => x.DeviceId == deviceId.Value);

            if (start.HasValue)
                query = query.Where(x => x.StartTime >= start.Value);

            if (end.HasValue)
                query = query.Where(x => x.StartTime < end.Value);

            return await query
                .OrderByDescending(x => x.StartTime)
                .Take(10000)
                .Select(x => new AppUsageResponse
                {
                    Id = x.Id,
                    AppId = x.AppId!.Value,
                    AppName = x.App!.Name,
                    Title = x.Title,
                    StartTime = x.StartTime,
                    EndTime = x.EndTime,
                    DurationSeconds = x.DurationSeconds
                })
                .ToListAsync();
        }
    }
}
