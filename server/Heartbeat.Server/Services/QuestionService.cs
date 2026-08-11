using System.Text.Json;
using Heartbeat.Core;
using Heartbeat.Core.DTOs.Knowledge;
using Heartbeat.Server.Data;
using Heartbeat.Server.Entities;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Services
{
    /// <summary>
    /// 发问编排（ADR-029 §4，证据卡随 ADR-031 §6）：缓存判读 → 装配 digest（与叙事同一份）→
    /// 判官提名候选 → 服务端从真实 segments 物化 ActivityCluster 证据卡 → 封顶落缓存。
    /// 缓存契约与 recap 同构：历史窗口命中即回；今日按水位（落后 >1h 重生成）；空日不调 LLM；
    /// 失败不写缓存；payload 版本不符视为未命中（旧单阶段表单安全失效，ADR-031 迁移）。
    /// 读取时对已裁决 Matcher 做确定性 diff 过滤；RecurrenceProbe 命中读时确定性追加
    /// （零 LLM，命中后果与 StrandMatcher 区分：只问"是否再次出现"）。
    /// </summary>
    public class QuestionService(
        AppDbContext db, DigestAssembler assembler, IAskingGenerator asking, TimeProvider? clock = null)
    {
        /// <summary>每天最多进队列的 cluster 问题数（ADR-029 §4 封顶）。确定性层对判官输出裁剪。</summary>
        private const int MaxQuestions = 3;

        /// <summary>recurrence 问题的读时封顶（与 cluster 独立——确定性生成，不占判官配额）。</summary>
        private const int MaxRecurrenceQuestions = 3;

        /// <summary>今日缓存的新鲜度护栏（与 RecapService 同值）：水位落后超过此值才重新发问。</summary>
        private static readonly TimeSpan FreshnessThreshold = TimeSpan.FromHours(1);

        private readonly TimeProvider _clock = clock ?? TimeProvider.System;

        public async Task<AskingQuestionsResponse> GetDailyQuestionsAsync(
            string ownerId, DateTimeOffset date, CancellationToken ct = default)
        {
            var window = DateRange.Day(date);
            DateTimeOffset windowStart = window.UtcStart;
            DateTimeOffset windowEnd = window.UtcEnd;

            var cached = await db.DailyQuestionSets
                .FirstOrDefaultAsync(q => q.OwnerId == ownerId && q.WindowStart == windowStart, ct);

            if (IsCurrent(cached) && await IsFreshAsync(ownerId, windowStart, windowEnd, cached!, ct))
                return await ComposeAsync(ownerId, window, cached!, ct);

            var projection = await assembler.AssembleAsync(ownerId, window, date.Offset, ct);
            if (projection.IsEmpty)
                return new AskingQuestionsResponse();

            var context = await assembler.LoadAskingContextAsync(ownerId, ct);
            var candidates = await asking.AskAsync(projection.Digest, context, ct);
            if (candidates == null)
            {
                // 判官失败（含未配置）：不写缓存；有当前版本旧缓存回旧缓存，没有则安静空手。
                return IsCurrent(cached)
                    ? await ComposeAsync(ownerId, window, cached!, ct)
                    : new AskingQuestionsResponse();
            }

            // 证据物化（ADR-031 §6）：谓词在当日真实 segments 上零命中的候选整个丢弃——
            // 发出去的每张证据卡都可核对。
            var segments = await QuerySegmentsAsync(ownerId, windowStart, windowEnd, ct);
            var depthTables = await assembler.LoadDepthTablesAsync(ct);
            var questions = candidates
                .Select(c => ActivityClusterEvidence.Materialize(
                    Guid.CreateVersion7(), AskingQuestionKinds.Cluster, c.Question, c.Matcher,
                    segments, window, depthTables))
                .Where(q => q != null)
                .Select(q => q!)
                .Take(MaxQuestions)
                .ToList();

            if (cached == null)
            {
                cached = new DailyQuestionSet { OwnerId = ownerId, WindowStart = windowStart };
                db.DailyQuestionSets.Add(cached);
            }
            cached.PayloadVersion = DailyQuestionSet.CurrentPayloadVersion;
            cached.PayloadJson = JsonSerializer.Serialize(questions);
            cached.SegmentWatermark = projection.SegmentWatermarkUtc;
            cached.GeneratedAt = _clock.GetUtcNow();
            await db.SaveChangesAsync(ct);

            return await ComposeAsync(ownerId, window, cached, ct);
        }

        /// <summary>
        /// 第二阶段取证（ADR-031 §6）：按 (Owner, 日窗口, 问题 Id) 取回服务端自己发出的证据卡。
        /// cluster 从缓存 payload 找；recurrence 按 ProbeId 对当日证据确定性重物化。
        /// 找不到（过期重生成 / 伪造 Id / 已裁决）返回 null——proposal 只能解释用户实际看过的证据。
        /// </summary>
        public async Task<AskingQuestionResponse?> FindQuestionAsync(
            string ownerId, DateTimeOffset date, Guid questionId, CancellationToken ct = default)
        {
            var composed = await GetDailyQuestionsAsync(ownerId, date, ct);
            return composed.Questions.FirstOrDefault(q => q.Id == questionId);
        }

        /// <summary>版本不符（含旧单阶段 payload 的隐式版本 0）按未命中处理：读取时安全重生成，不迁移机器提案。</summary>
        private static bool IsCurrent(DailyQuestionSet? cached)
            => cached != null && cached.PayloadVersion == DailyQuestionSet.CurrentPayloadVersion;

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
        /// 读时组装（ADR-029 §4 + ADR-031 §5）：缓存 cluster 问题过已裁决 diff，
        /// 再确定性追加当日命中的活跃 Probe 的 recurrence 问题。无会话态——
        /// 没答完的问题下次照常端上来，diff 本身就是"续上"机制。
        /// </summary>
        private async Task<AskingQuestionsResponse> ComposeAsync(
            string ownerId, DateRange window, DailyQuestionSet cached, CancellationToken ct)
        {
            List<AskingQuestionResponse> items;
            try
            {
                items = JsonSerializer.Deserialize<List<AskingQuestionResponse>>(cached.PayloadJson) ?? [];
            }
            catch (JsonException)
            {
                items = [];
            }

            var adjudicated = await assembler.LoadAdjudicatedAsync(ownerId, ct);
            var activeProbePredicates = await db.RecurrenceProbes
                .Where(p => p.OwnerId == ownerId && p.Status == ProbeStatuses.Active)
                .Select(p => new { p.Source, p.StepsJson })
                .ToListAsync(ct);
            var probeSet = activeProbePredicates.Select(p => (p.Source, p.StepsJson)).ToHashSet();

            // cluster 问题剔除：已裁决（绑定/静音）的别再端上来；与活跃 Probe 同谓词的让位给
            // recurrence 问题（命中后果不同，不重复问两遍）。
            var remaining = items
                .Where(i => MatcherNormalizer.Normalize(i.Matcher) is { } n
                            && !adjudicated.Contains((n.Source, MatcherCodec.Serialize(n.Steps)))
                            && !probeSet.Contains((n.Source, MatcherCodec.Serialize(n.Steps))))
                .ToList();

            remaining.AddRange(await ComposeRecurrenceAsync(ownerId, window, ct));
            if (remaining.Count == 0) return new AskingQuestionsResponse();

            // 读数展示名随声明走（ADR-030 §7）：前端不再持硬编码标签字典。
            var labels = (await assembler.LoadDepthTablesAsync(ct)).Labels();
            return new AskingQuestionsResponse
            {
                Questions = remaining,
                ReadingLabels = new Dictionary<string, string>(labels),
            };
        }

        /// <summary>
        /// RecurrenceProbe 命中 → recurrence 问题（ADR-031 §5，零 LLM）：只通知"某个未归属
        /// Episode 可能再次出现"，不注入旧 Episode 到 Recap、不自动建 Strand、不自动关联。
        /// 问题 Id 恒等于 ProbeId：确定性、可在第二阶段按库中对象校验 Owner。
        /// </summary>
        private async Task<List<AskingQuestionResponse>> ComposeRecurrenceAsync(
            string ownerId, DateRange window, CancellationToken ct)
        {
            var probes = await db.RecurrenceProbes
                .Include(p => p.Episode)
                .Where(p => p.OwnerId == ownerId && p.Status == ProbeStatuses.Active)
                .OrderBy(p => p.Id)
                .ToListAsync(ct);
            if (probes.Count == 0) return [];

            var segments = await QuerySegmentsAsync(ownerId, window.UtcStart, window.UtcEnd, ct);
            if (segments.Count == 0) return [];
            var depthTables = await assembler.LoadDepthTablesAsync(ct);

            var result = new List<AskingQuestionResponse>();
            foreach (var probe in probes)
            {
                if (result.Count >= MaxRecurrenceQuestions) break;
                var matcher = new MatcherDto
                {
                    Source = probe.Source,
                    Steps = MatcherCodec.Deserialize(probe.StepsJson),
                };
                var question = ActivityClusterEvidence.Materialize(
                    probe.Id, AskingQuestionKinds.Recurrence,
                    $"「{probe.Episode.Text}」（{probe.Episode.LocalDate:yyyy-MM-dd}）似乎又出现了——这次还是同一件事吗？",
                    matcher, segments, window, depthTables);
                if (question == null) continue;

                question.ProbeId = probe.Id;
                question.EpisodeId = probe.EpisodeId;
                question.EpisodeText = probe.Episode.Text;
                question.EpisodeDate = probe.Episode.LocalDate;
                result.Add(question);
            }
            return result;
        }

        /// <summary>证据物化的取数：与 DigestAssembler 同一窗口规则（区间重叠 + 零长度点事件归窗）。</summary>
        private async Task<List<RecapSegmentInput>> QuerySegmentsAsync(
            string ownerId, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken ct)
        {
            return await db.ActivitySegments
                .Where(x => x.Device.OwnerId == ownerId)
                .Where(x => x.EndTime > windowStart && x.StartTime < windowEnd
                            || x.StartTime == x.EndTime && x.StartTime >= windowStart && x.StartTime < windowEnd)
                .Select(x => new RecapSegmentInput(
                    x.Device.DeviceName,
                    x.Source,
                    x.IdentityKey,
                    x.AppIdentityId != null
                        ? x.AppIdentity!.App.DisplayName
                        : x.App != null ? x.App.DisplayName : null,
                    x.Title,
                    x.StartTime,
                    x.EndTime,
                    x.Attributes))
                .ToListAsync(ct);
        }
    }
}
