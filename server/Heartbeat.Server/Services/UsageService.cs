using Heartbeat.Core;
using Heartbeat.Core.DTOs.Apps;
using Heartbeat.Core.DTOs.Segments;
using Heartbeat.Server.Data;
using Heartbeat.Server.Entities;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Services
{
    public class UsageService(
        AppDbContext db,
        AppIdentityService? appIdentityService = null,
        ILogger<UsageService>? logger = null)
    {
        private readonly AppDbContext _db = db;
        private readonly AppIdentityService _appIdentityService = appIdentityService ?? new AppIdentityService(db);

        /// <summary>
        /// 统一摄入例程（ADR-018）：校验 → App 关联 → 按 Id 快照 upsert。
        /// 唯一上传入口 /segments（ADR-020）：system 段与插件段同形，IdentityKey 由采集端计算。
        /// Id 即活动身份：已有行则扩展边界（EndTime 取 max、attributes 后写胜），新 Id 插入。
        /// 快照单调生长，摄入可交换可重入——乱序重传、批内多快照同 Id 均收敛到同一行。
        /// </summary>
        public async Task SaveSegmentsAsync(long deviceId, List<ActivitySegmentItem> segments)
        {
            SegmentIngestContract.Validate(segments);

            var ordered = segments.OrderBy(s => s.StartTime).ToList();

            // 快照 upsert：一次批量取回本批涉及的已有行，新插入的行也进字典，
            // 让批内后续同 Id 快照走扩展路径（枢纽攒批场景）。
            var ids = ordered.Select(s => s.Id).Distinct().ToList();
            var rows = await _db.ActivitySegments
                .Where(x => ids.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id);

            foreach (var group in ordered.GroupBy(s => s.Id))
            {
                var expectedDeviceId = deviceId;
                var expectedSource = group.First().Source;
                var expectedIdentityKey = group.First().IdentityKey;
                if (rows.TryGetValue(group.Key, out var existing))
                {
                    expectedDeviceId = existing.DeviceId;
                    expectedSource = existing.Source;
                    expectedIdentityKey = existing.IdentityKey;
                }

                if (expectedDeviceId == deviceId
                    && group.All(item =>
                        string.Equals(expectedSource, item.Source, StringComparison.Ordinal)
                        && string.Equals(expectedIdentityKey, item.IdentityKey, StringComparison.Ordinal)))
                {
                    continue;
                }

                logger?.LogWarning(
                    "段 {Id} 身份不匹配，整批拒收: 预期 ({DeviceId}, {Source}, {Key})",
                    group.Key, expectedDeviceId, expectedSource, expectedIdentityKey);
                throw new SegmentIngestContractException(
                    SegmentIngestContractViolation.IdentityConflict,
                    $"Segment {group.Key} conflicts with its existing device, source, or identity key.");
            }

            // 只为新事实解析身份；被 identity guard 拒绝的旧 Id 不得制造 provisional App。
            var identityByItem = new Dictionary<ActivitySegmentItem, AppIdentity?>();
            foreach (var item in ordered.Where(x => !rows.ContainsKey(x.Id)))
            {
                var key = ResolveIdentityKey(item);
                if (key == null)
                {
                    identityByItem[item] = null;
                    continue;
                }

                identityByItem[item] = await _appIdentityService.ResolveAsync(key, item.AppDisplayName);
            }

            foreach (var s in ordered)
            {
                if (rows.TryGetValue(s.Id, out var row))
                {
                    // 后写胜只对"最新快照"生效：乱序到达的旧快照不得回退 Title/Attributes。
                    var isNewest = s.EndTime >= row.EndTime;
                    if (s.StartTime < row.StartTime) row.StartTime = s.StartTime;
                    if (s.EndTime > row.EndTime) row.EndTime = s.EndTime;
                    if (isNewest)
                    {
                        if (s.Title != null) row.Title = s.Title;
                        if (s.Attributes.HasValue) row.Attributes = s.Attributes.Value.GetRawText();
                    }
                }
                else
                {
                    identityByItem.TryGetValue(s, out var appIdentity);
                    var entity = new ActivitySegment
                    {
                        Id = s.Id,
                        DeviceId = deviceId,
                        Source = s.Source,
                        IdentityKey = s.IdentityKey,
                        AppIdentityId = appIdentity?.Id,
                        // expand 双写：旧消费者仍读 AppId；产品语义的权威路径是 AppIdentity.AppId。
                        AppId = appIdentity?.AppId,
                        Title = s.Title,
                        StartTime = s.StartTime,
                        EndTime = s.EndTime,
                        Attributes = s.Attributes?.GetRawText()
                    };
                    _db.ActivitySegments.Add(entity);
                    rows[s.Id] = entity;
                }
            }

            await _db.SaveChangesAsync();
        }

        private static string? ResolveIdentityKey(ActivitySegmentItem item)
        {
            return string.IsNullOrWhiteSpace(item.AppIdentityKey) ? null : item.AppIdentityKey;
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
                .Where(x => x.Device.OwnerId == ownerId)
                .AsQueryable();

            query = string.IsNullOrWhiteSpace(source)
                ? query.Where(x => x.Source != ActivitySources.System)
                : query.Where(x => x.Source == source);

            if (deviceId.HasValue)
                query = query.Where(x => x.DeviceId == deviceId.Value);

            if (appId.HasValue)
                query = query.Where(x =>
                    (x.AppIdentityId != null ? x.AppIdentity!.AppId : x.AppId) == appId.Value);

            // 区间重叠语义（ADR-018 §4）：跨窗长段在其覆盖的每个窗口都可见。
            // 下界用 >= 而非 >：零长度点事件恰落在窗口起点时不丢
            //（代价是恰好首尾贴边的段以零重叠出现，回放按时间轴裁剪无感）。
            if (start.HasValue)
                query = query.Where(x => x.EndTime >= start.Value);

            if (end.HasValue)
                query = query.Where(x => x.StartTime < end.Value);

            return await query
                .OrderByDescending(x => x.StartTime)
                .Take(10000)
                .Select(x => new SegmentResponse
                {
                    Id = x.Id,
                    DeviceId = x.DeviceId,
                    Source = x.Source,
                    IdentityKey = x.IdentityKey,
                    AppId = x.AppIdentityId != null ? x.AppIdentity!.AppId : x.AppId,
                    AppKey = x.AppIdentityId != null
                        ? x.AppIdentity!.App.Key
                        : x.App != null ? x.App.Key : null,
                    AppDisplayName = x.AppIdentityId != null
                        ? x.AppIdentity!.App.DisplayName
                        : x.App != null ? x.App.DisplayName : null,
                    AppName = x.AppIdentityId != null
                        ? x.AppIdentity!.App.DisplayName
                        : x.App != null ? x.App.DisplayName : null,
                    AppIdentityId = x.AppIdentityId,
                    AppIdentityKey = x.AppIdentity != null ? x.AppIdentity.Key : null,
                    Title = x.Title,
                    StartTime = x.StartTime,
                    EndTime = x.EndTime,
                    // 时长是派生量（ADR-018）：不落盘，投影现算
                    DurationSeconds = (int)(x.EndTime - x.StartTime).TotalSeconds,
                    Attributes = x.Attributes
                })
                .ToListAsync();
        }

        public async Task<List<AppUsageResponse>> GetUsageAsync(string ownerId, long? deviceId, DateTimeOffset? start, DateTimeOffset? end)
        {
            var query = _db.ActivitySegments
                .Where(x => x.Device.OwnerId == ownerId)
                .Where(x => x.Source == ActivitySources.System)
                .AsQueryable();

            if (deviceId.HasValue)
                query = query.Where(x => x.DeviceId == deviceId.Value);

            // 区间重叠语义（ADR-018 §4）。system 段无零长度（≥1s），下界用严格 >，
            // 避免恰在窗口起点结束的段以零重叠混入列表。
            if (start.HasValue)
                query = query.Where(x => x.EndTime > start.Value);

            if (end.HasValue)
                query = query.Where(x => x.StartTime < end.Value);

            return await query
                .OrderByDescending(x => x.StartTime)
                .Take(10000)
                .Select(x => new AppUsageResponse
                {
                    Id = x.Id,
                    DeviceId = x.DeviceId,
                    AppId = (x.AppIdentityId != null ? x.AppIdentity!.AppId : x.AppId)!.Value,
                    AppKey = x.AppIdentityId != null ? x.AppIdentity!.App.Key : x.App!.Key,
                    AppDisplayName = x.AppIdentityId != null
                        ? x.AppIdentity!.App.DisplayName
                        : x.App!.DisplayName,
                    AppName = x.AppIdentityId != null
                        ? x.AppIdentity!.App.DisplayName
                        : x.App!.DisplayName,
                    AppIdentityId = x.AppIdentityId,
                    AppIdentityKey = x.AppIdentity != null ? x.AppIdentity.Key : null,
                    Title = x.Title,
                    StartTime = x.StartTime,
                    EndTime = x.EndTime,
                    DurationSeconds = (int)(x.EndTime - x.StartTime).TotalSeconds
                })
                .ToListAsync();
        }
    }
}
