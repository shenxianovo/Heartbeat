using Heartbeat.Core;
using Heartbeat.Core.DTOs.Recaps;
using Heartbeat.Server.Data;
using Heartbeat.Server.Entities;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Services
{
    /// <summary>
    /// 每日 Recap 编排（ADR-023）：缓存判读 → 投影 → 生成 → upsert。
    /// 历史窗口命中即回；今日窗口按水位（落后 >1h 重生成）；空日不调 LLM 不写缓存；失败不写缓存。
    /// </summary>
    public class RecapService(AppDbContext db, IRecapGenerator generator, TimeProvider? clock = null)
    {
        /// <summary>今日缓存的新鲜度护栏：水位落后超过此值才重生成（防轮询烧 token，非产品语义）。</summary>
        private static readonly TimeSpan FreshnessThreshold = TimeSpan.FromHours(1);

        private readonly AppDbContext _db = db;
        private readonly IRecapGenerator _generator = generator;
        private readonly TimeProvider _clock = clock ?? TimeProvider.System;

        public async Task<DailyRecapResponse> GetDailyRecapAsync(
            string ownerId, DateTimeOffset date, bool force, CancellationToken ct = default)
        {
            var window = DateRange.Day(date);
            DateTimeOffset windowStart = window.UtcStart;
            DateTimeOffset windowEnd = window.UtcEnd;

            var cached = await _db.Recaps
                .FirstOrDefaultAsync(r => r.OwnerId == ownerId && r.WindowStart == windowStart, ct);

            if (cached != null && !force && await IsFreshAsync(ownerId, windowStart, windowEnd, cached, ct))
                return ToResponse(date, cached);

            var segments = await QuerySegmentsAsync(ownerId, windowStart, windowEnd, ct);
            var knownStrands = await LoadKnownStrandsAsync(ownerId, ct);
            var projection = RecapProjection.Project(segments, window, date.Offset, knownStrands);

            if (projection.IsEmpty)
                return new DailyRecapResponse { Date = FormatDate(date), IsEmpty = true };

            var narrative = await _generator.GenerateAsync(projection.Digest, ct);

            if (cached == null)
            {
                cached = new Recap { OwnerId = ownerId, WindowStart = windowStart };
                _db.Recaps.Add(cached);
            }
            cached.Narrative = narrative;
            cached.GeneratedAt = _clock.GetUtcNow();
            cached.Model = _generator.Model;
            cached.PromptHash = _generator.PromptHash;
            cached.SegmentWatermark = projection.SegmentWatermarkUtc;
            await _db.SaveChangesAsync(ct);

            return ToResponse(date, cached);
        }

        /// <summary>
        /// 公开视角只读已有缓存：不查询段、不判断水位、不调用生成器。
        /// 未生成过的日期返回 null，由公开端点映射为 404，前端不渲染卡片。
        /// </summary>
        public async Task<DailyRecapResponse?> GetCachedDailyRecapAsync(
            string ownerId, DateTimeOffset date, CancellationToken ct = default)
        {
            var windowStart = DateRange.Day(date).UtcStart;
            var cached = await _db.Recaps
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.OwnerId == ownerId && r.WindowStart == windowStart, ct);

            return cached == null ? null : ToResponse(date, cached);
        }

        private async Task<bool> IsFreshAsync(
            string ownerId, DateTimeOffset windowStart, DateTimeOffset windowEnd, Recap cached, CancellationToken ct)
        {
            // 已结束的窗口是历史：命中即回，永不过期（离线重传的迟到段由用户显式重生成收敛）。
            if (_clock.GetUtcNow() >= windowEnd) return true;

            var latestEnd = await _db.ActivitySegments
                .Where(x => x.Device.OwnerId == ownerId)
                .Where(x => x.EndTime > windowStart && x.StartTime < windowEnd)
                .MaxAsync(x => (DateTimeOffset?)x.EndTime, ct) ?? windowStart;
            if (latestEnd > windowEnd) latestEnd = windowEnd;

            return latestEnd - cached.SegmentWatermark <= FreshnessThreshold;
        }

        private async Task<List<RecapSegmentInput>> QuerySegmentsAsync(
            string ownerId, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken ct)
        {
            // 与投影同一套窗口规则：区间重叠，零长度点事件按落点归窗。
            return await _db.ActivitySegments
                .Where(x => x.Device.OwnerId == ownerId)
                .Where(x => x.EndTime > windowStart && x.StartTime < windowEnd
                            || x.StartTime == x.EndTime && x.StartTime >= windowStart && x.StartTime < windowEnd)
                .Select(x => new RecapSegmentInput(
                    x.Device.DeviceName,
                    x.Source,
                    x.IdentityKey,
                    x.App != null ? x.App.Name : null,
                    x.Title,
                    x.StartTime,
                    x.EndTime,
                    x.Attributes))
                .ToListAsync(ct);
        }

        /// <summary>
        /// 载入 Owner 的 Strand 成员，铺成 把手 → (名字, 释义) 映射，供投影反哺（ADR-028 §6）。
        /// 同一把手落在多个 Strand 时后写胜——多对多消歧本属提问器职责，投影只做展示。
        /// </summary>
        private async Task<Dictionary<HandleRef, StrandGloss>> LoadKnownStrandsAsync(string ownerId, CancellationToken ct)
        {
            var members = await _db.StrandHandles
                .Where(m => m.Strand.OwnerId == ownerId)
                .Select(m => new { m.Source, m.Token, m.Strand.Name, m.Strand.Gloss })
                .ToListAsync(ct);

            var map = new Dictionary<HandleRef, StrandGloss>();
            foreach (var m in members)
                map[new HandleRef(m.Source, m.Token)] = new StrandGloss(m.Name, m.Gloss);
            return map;
        }

        private static DailyRecapResponse ToResponse(DateTimeOffset date, Recap recap) => new()
        {
            Date = FormatDate(date),
            IsEmpty = false,
            Narrative = recap.Narrative,
            GeneratedAt = recap.GeneratedAt,
            Model = recap.Model
        };

        private static string FormatDate(DateTimeOffset date) => date.Date.ToString("yyyy-MM-dd");
    }
}
