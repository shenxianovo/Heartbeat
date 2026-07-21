using System.Text.Json;
using Heartbeat.Core;
using Heartbeat.Core.DTOs.Knowledge;
using Heartbeat.Server.Data;
using Heartbeat.Server.Entities;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Services
{
    /// <summary>
    /// 发问编排（ADR-029 §4）：缓存判读 → 装配 digest（与叙事同一份）→ 判官单调用 → 封顶落缓存。
    /// 缓存契约与 recap 同构：历史窗口命中即回；今日按水位（落后 >1h 重生成）；空日不调 LLM；失败不写缓存。
    /// 读取时对已裁决 Matcher 做确定性 diff 过滤——裁决后的"别再端上来"零 LLM 重调。
    /// </summary>
    public class QuestionService(
        AppDbContext db, DigestAssembler assembler, IAskingGenerator asking, TimeProvider? clock = null)
    {
        /// <summary>每天最多进队列的问题数（ADR-029 §4 封顶）。确定性层对判官输出裁剪。</summary>
        private const int MaxQuestions = 3;

        /// <summary>今日缓存的新鲜度护栏（与 RecapService 同值）：水位落后超过此值才重新发问。</summary>
        private static readonly TimeSpan FreshnessThreshold = TimeSpan.FromHours(1);

        private readonly TimeProvider _clock = clock ?? TimeProvider.System;

        public async Task<DailyQuestionsResponse> GetDailyQuestionsAsync(
            string ownerId, DateTimeOffset date, CancellationToken ct = default)
        {
            var window = DateRange.Day(date);
            DateTimeOffset windowStart = window.UtcStart;
            DateTimeOffset windowEnd = window.UtcEnd;

            var cached = await db.DailyQuestionSets
                .FirstOrDefaultAsync(q => q.OwnerId == ownerId && q.WindowStart == windowStart, ct);

            if (cached != null && await IsFreshAsync(ownerId, windowStart, windowEnd, cached, ct))
                return await FilterAdjudicatedAsync(ownerId, cached, ct);

            var projection = await assembler.AssembleAsync(ownerId, window, date.Offset, ct);
            if (projection.IsEmpty)
                return new DailyQuestionsResponse();

            var context = await assembler.LoadAskingContextAsync(ownerId, ct);
            var questions = await asking.AskAsync(projection.Digest, context, ct);
            if (questions == null)
            {
                // 判官失败（含未配置）：不写缓存；有旧缓存回旧缓存，没有则安静空手。
                return cached != null
                    ? await FilterAdjudicatedAsync(ownerId, cached, ct)
                    : new DailyQuestionsResponse();
            }

            if (cached == null)
            {
                cached = new DailyQuestionSet { OwnerId = ownerId, WindowStart = windowStart };
                db.DailyQuestionSets.Add(cached);
            }
            cached.PayloadJson = JsonSerializer.Serialize(questions.Take(MaxQuestions).ToList());
            cached.SegmentWatermark = projection.SegmentWatermarkUtc;
            cached.GeneratedAt = _clock.GetUtcNow();
            await db.SaveChangesAsync(ct);

            return await FilterAdjudicatedAsync(ownerId, cached, ct);
        }

        private async Task<bool> IsFreshAsync(
            string ownerId, DateTimeOffset windowStart, DateTimeOffset windowEnd,
            DailyQuestionSet cached, CancellationToken ct)
        {
            // 已结束的窗口是历史：命中即回（该问的当天问，过后不追问）。
            if (_clock.GetUtcNow() >= windowEnd) return true;

            var latestEnd = await assembler.LatestSegmentEndAsync(ownerId, windowStart, windowEnd, ct);
            return latestEnd - cached.SegmentWatermark <= FreshnessThreshold;
        }

        /// <summary>
        /// 读时确定性 diff（ADR-029 §4）：缓存问题里锚定 Matcher 已被裁决（绑定或静音）的条目剔除。
        /// 无会话态——没答完的问题下次照常端上来，diff 本身就是"续上"机制。
        /// </summary>
        private async Task<DailyQuestionsResponse> FilterAdjudicatedAsync(
            string ownerId, DailyQuestionSet cached, CancellationToken ct)
        {
            List<QuestionItemResponse> items;
            try
            {
                items = JsonSerializer.Deserialize<List<QuestionItemResponse>>(cached.PayloadJson) ?? [];
            }
            catch (JsonException)
            {
                return new DailyQuestionsResponse();
            }
            if (items.Count == 0) return new DailyQuestionsResponse();

            var adjudicated = await assembler.LoadAdjudicatedAsync(ownerId, ct);
            var remaining = items
                .Where(i => MatcherNormalizer.Normalize(i.Matcher) is { } n
                            && !adjudicated.Contains((n.Source, MatcherCodec.Serialize(n.Steps))))
                .ToList();
            return new DailyQuestionsResponse { Questions = remaining };
        }
    }
}
