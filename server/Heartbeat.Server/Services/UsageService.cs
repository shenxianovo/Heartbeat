using System.Text.Json;
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
        TimeProvider? timeProvider = null)
    {
        private readonly AppDbContext _db = db;
        private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

        /// <summary>
        /// 升级前段缓存的严格历史导入入口；Fact Store 直接保存家族事实。
        /// 新 Collector 数据使用原生 Fact 摄入，不通过此处的旧快照生长规则。
        /// </summary>
        public async Task SaveSegmentsAsync(long deviceId, List<ActivitySegmentItem> segments)
        {
            SegmentIngestContract.Validate(segments, _timeProvider.GetUtcNow());
            await SaveValidatedSegmentsAsync(deviceId, segments);
        }

        internal Task SaveValidatedSegmentsAsync(long deviceId, List<ActivitySegmentItem> segments) =>
            new FactStore(_db, _timeProvider).ImportSegmentsAsync(deviceId, segments, validated: true);

        /// <summary>
        /// 插件段查询（ADR-017 §4）：回放多轨用。默认返回全部非 system source
        /// （system 轨走 GetUsageAsync，两者互补不重叠）；source 指定时只查该轨。
        /// </summary>
        public async Task<List<SegmentResponse>> GetSegmentsAsync(
            string ownerId, long? deviceId, string? source, long? appId,
            DateTimeOffset? start, DateTimeOffset? end)
        {
            var query = _db.ActivitySegments
                .Where(x => x.OwnerId == ownerId)
                .AsQueryable();

            query = string.IsNullOrWhiteSpace(source)
                ? query.Where(x => x.Source != ActivitySources.System)
                : query.Where(x => x.Source == source);

            if (deviceId.HasValue)
                query = query.Where(x => x.DeviceId == deviceId.Value);

            if (appId.HasValue)
                query = query.Where(x =>
                    (x.TargetKind != "account" && x.AppIdentityId != null ? x.AppIdentity!.AppId : x.AppId) == appId.Value);

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
                    ObserverId = x.ObserverId, TargetKind = x.TargetKind, TargetId = x.TargetId,
                    TargetName = x.TargetName ?? (x.TargetKind == "application-context" && x.App != null && x.Device != null ? x.Device.DeviceName + " / " + x.App.DisplayName : x.TargetKind == "device" && x.Device != null ? x.Device.DeviceName : null),
                    Id = x.Id,
                    DeviceId = x.DeviceId,
                    Source = x.Source,
                    IdentityKey = x.IdentityKey,
                    AppId = x.TargetKind != "account" && x.AppIdentityId != null ? x.AppIdentity!.AppId : x.AppId,
                    AppKey = x.TargetKind != "account" && x.AppIdentityId != null
                        ? x.AppIdentity!.App.Key
                        : x.App != null ? x.App.Key : null,
                    AppDisplayName = x.TargetKind != "account" && x.AppIdentityId != null
                        ? x.AppIdentity!.App.DisplayName
                        : x.App != null ? x.App.DisplayName : null,
                    AppName = x.TargetKind != "account" && x.AppIdentityId != null
                        ? x.AppIdentity!.App.DisplayName
                        : x.App != null ? x.App.DisplayName : null,
                    AppIdentityId = x.AppIdentityId,
                    AppIdentityKey = x.AppIdentity != null ? x.AppIdentity.Key : null,
                    Title = x.Title,
                    StartTime = x.StartTime,
                    EndTime = x.EndTime,
                    // 时长是派生量（ADR-018）：不落盘，投影现算
                    DurationSeconds = (int)(x.EndTime - x.StartTime).TotalSeconds,
                    Payload = ParsePayload(x.Payload),
                    StreamId = x.StreamId,
                    FactId = x.FactId,
                    Revision = x.Revision,
                    Origin = x.Stream.Origin,
                    LegacySubjectId = x.TargetKind == null ? x.Stream.SubjectId : null,
                    LegacySubjectKind = x.TargetKind == null ? x.Stream.Subject.Kind : null,
                    LegacySubjectName = x.TargetKind != null ? null : x.Device != null ? x.Device.DeviceName
                        : x.Stream.Subject.DisplayName
                })
                .ToListAsync();
        }

        public async Task<List<AppUsageResponse>> GetUsageAsync(string ownerId, long? deviceId, DateTimeOffset? start, DateTimeOffset? end)
        {
            var query = _db.ActivitySegments
                .Where(x => x.OwnerId == ownerId)
                .Where(x => x.Source == ActivitySources.System && x.DeviceId != null && x.AppIdentityId != null)
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
                    DeviceId = x.DeviceId!.Value,
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
        private static Dictionary<string, object?>? ParsePayload(string? payload)
            => payload == null ? null : JsonSerializer.Deserialize<Dictionary<string, object?>>(payload);

    }
}
