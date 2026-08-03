using Heartbeat.Core;
using Heartbeat.Core.DTOs.Knowledge;
using Heartbeat.Core.DTOs.Recaps;
using Heartbeat.Server.Data;
using Heartbeat.Server.Entities;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Services
{
    /// <summary>
    /// 每日 Recap 编排（ADR-023，知识判脏随 ADR-031 §7）：缓存判读 → 装配 digest（DigestAssembler，
    /// 与发问共用）→ 生成 → upsert。历史窗口命中即回；今日窗口按水位（落后 >1h 重生成）；
    /// 空日不调 LLM 不写缓存；失败不写缓存（不覆盖上次成功正文/投影）。
    /// 缓存命中时确定性重算日期知识投影标识：不同只回 stale hint，绝不自动调 LLM——
    /// Segment freshness（水位，自动重生成）与 knowledge freshness（hash，只提示）是两把独立的尺。
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
            {
                // 知识判脏只在认证读取时重算（确定性、零 LLM）；null hash 的旧行惰性视为可重新生成。
                var currentHash = await assembler.ComputeKnowledgeHashAsync(ownerId, window, date.Offset, ct);
                return ToResponse(date, cached, knowledgeStale: cached.KnowledgeHash != currentHash);
            }

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
            cached.KnowledgeHash = projection.KnowledgeHash;
            await _db.SaveChangesAsync(ct);

            return ToResponse(date, cached, knowledgeStale: false);
        }

        /// <summary>
        /// 公开视角只读已有缓存：不查询段、不读取私有知识、不重算投影、不调用生成器。
        /// 未生成过的日期返回 null，由公开端点映射为 404，前端不渲染卡片。
        /// </summary>
        public async Task<DailyRecapResponse?> GetCachedDailyRecapAsync(
            string ownerId, DateTimeOffset date, CancellationToken ct = default)
        {
            var windowStart = DateRange.Day(date).UtcStart;
            var cached = await _db.Recaps
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.OwnerId == ownerId && r.WindowStart == windowStart, ct);

            // KnowledgeStale 恒 false：判脏需要私有知识，公开路径不暴露投影细节。
            return cached == null ? null : ToResponse(date, cached, knowledgeStale: false);
        }

        private async Task<bool> IsFreshAsync(
            string ownerId, DateTimeOffset windowStart, DateTimeOffset windowEnd, Recap cached, CancellationToken ct)
        {
            // 已结束的窗口是历史：命中即回，段层面永不过期（离线重传的迟到段由用户显式重生成收敛）。
            if (_clock.GetUtcNow() >= windowEnd) return true;

            var latestEnd = await assembler.LatestSegmentEndAsync(ownerId, windowStart, windowEnd, ct);
            return latestEnd - cached.SegmentWatermark <= FreshnessThreshold;
        }

        private static DailyRecapResponse ToResponse(DateTimeOffset date, Recap recap, bool knowledgeStale) => new()
        {
            Date = FormatDate(date),
            IsEmpty = false,
            Narrative = recap.Narrative,
            GeneratedAt = recap.GeneratedAt,
            Model = recap.Model,
            KnowledgeStale = knowledgeStale
        };

        private static string FormatDate(DateTimeOffset date) => date.Date.ToString("yyyy-MM-dd");
    }
}
