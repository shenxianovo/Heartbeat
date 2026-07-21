using Heartbeat.Core;
using Heartbeat.Core.DTOs.Knowledge;
using Heartbeat.Core.DTOs.Recaps;
using Heartbeat.Server.Data;
using Heartbeat.Server.Entities;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Services
{
    /// <summary>
    /// 每日 Recap 编排（ADR-023）：缓存判读 → 装配 digest（DigestAssembler，与发问共用）→ 生成 → upsert。
    /// 历史窗口命中即回；今日窗口按水位（落后 >1h 重生成）；空日不调 LLM 不写缓存；失败不写缓存。
    /// </summary>
    public class RecapService(AppDbContext db, IRecapGenerator generator, DigestAssembler assembler, TimeProvider? clock = null)
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

            var projection = await assembler.AssembleAsync(ownerId, window, date.Offset, ct);

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

            var latestEnd = await assembler.LatestSegmentEndAsync(ownerId, windowStart, windowEnd, ct);
            return latestEnd - cached.SegmentWatermark <= FreshnessThreshold;
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
