using Heartbeat.Core;
using Heartbeat.Core.DTOs.Apps;
using Heartbeat.Core.DTOs.Segments;
using Heartbeat.Core.DTOs.Usage;
using Heartbeat.Server.Data;
using Heartbeat.Server.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Heartbeat.Server.Services
{
    public class UsageService(AppDbContext db, ILogger<UsageService>? logger = null)
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
                // 旧版 Agent 无 Id（Guid.Empty）→ 服务端代为生成。ADR-018 后不再续接，
                // 旧版按 flush 周期成行（碎片），随 Agent 自动更新自然消失。
                Id = u.Id != Guid.Empty ? u.Id : Guid.CreateVersion7(),
                Source = ActivitySources.System,
                IdentityKey = SystemIdentity.Key(u.AppName, u.Title),
                AppName = u.AppName,
                Title = u.Title,
                StartTime = u.StartTime,
                EndTime = u.EndTime
            }).ToList();

            await SaveSegmentsAsync(deviceId, items);
        }

        /// <summary>
        /// 统一摄入例程（ADR-018）：校验 → App 关联 → 按 Id 快照 upsert。
        /// Id 即活动身份：已有行则扩展边界（EndTime 取 max、attributes 后写胜），新 Id 插入。
        /// 快照单调生长，摄入可交换可重入——乱序重传、批内多快照同 Id 均收敛到同一行。
        /// </summary>
        public async Task SaveSegmentsAsync(long deviceId, List<ActivitySegmentItem> segments)
        {
            var valid = SegmentValidationPolicy.Filter(segments, DateTimeOffset.UtcNow);
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

            // 快照 upsert：一次批量取回本批涉及的已有行，新插入的行也进字典，
            // 让批内后续同 Id 快照走扩展路径（枢纽攒批场景）。
            var ids = valid.Select(s => s.Id).Distinct().ToList();
            var rows = await _db.ActivitySegments
                .Where(x => ids.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id);

            foreach (var s in valid)
            {
                if (rows.TryGetValue(s.Id, out var row))
                {
                    // 身份守卫（ADR-018 §2）：同 Id 必须同设备、同 Source、同 IdentityKey，
                    // 失控采集器复用 Id 只能命中自己的行，无法污染他行。
                    if (row.DeviceId != deviceId
                        || !string.Equals(row.Source, s.Source, StringComparison.Ordinal)
                        || !string.Equals(row.IdentityKey, s.IdentityKey, StringComparison.Ordinal))
                    {
                        logger?.LogWarning(
                            "段 {Id} 身份不匹配被拒收: 既有 ({Source}, {Key}) vs 传入 ({NewSource}, {NewKey})",
                            s.Id, row.Source, row.IdentityKey, s.Source, s.IdentityKey);
                        continue;
                    }

                    // 后写胜只对"最新快照"生效：乱序到达的旧快照不得回退 Title/Attributes。
                    var isNewest = s.EndTime >= row.EndTime;
                    if (s.StartTime < row.StartTime) row.StartTime = s.StartTime;
                    if (s.EndTime > row.EndTime) row.EndTime = s.EndTime;
                    row.DurationSeconds = (int)(row.EndTime - row.StartTime).TotalSeconds;
                    if (isNewest)
                    {
                        if (s.Title != null) row.Title = s.Title;
                        if (s.Attributes.HasValue) row.Attributes = s.Attributes.Value.GetRawText();
                    }
                }
                else
                {
                    var entity = new ActivitySegment
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
                    };
                    _db.ActivitySegments.Add(entity);
                    rows[s.Id] = entity;
                }
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
