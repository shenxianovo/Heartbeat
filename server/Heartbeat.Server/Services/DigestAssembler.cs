using System.Text.Json;
using Heartbeat.Core;
using Heartbeat.Core.DTOs.Collectors;
using Heartbeat.Core.DTOs.Knowledge;
using Heartbeat.Server.Calendar;
using Heartbeat.Server.Data;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Heartbeat.Server.Services
{
    /// <summary>
    /// 当日 digest 的取数装配（ADR-029 §4）：segments + Strand 指纹 + 近 14 天高频注释 → 同一份投影。
    /// 叙事（RecapService）与发问（QuestionService）共用，保证两次 LLM 调用吃字节相同的 digest。
    /// </summary>
    public class DigestAssembler(AppDbContext db)
    {
        private const int RecurringLookbackDays = 14;

        /// <summary>lookback 内出现天数达到此值的 L1 读数视为高频（常驻基础设施注释）。</summary>
        private const int RecurringMinDays = 8;

        /// <summary>few-shot 裁决日志各方向的行数上限（控 prompt 体积）。</summary>
        private const int MaxExampleLines = 20;

        public async Task<RecapProjectionResult> AssembleAsync(
            string ownerId, DateRange window, TimeSpan displayOffset, CancellationToken ct = default)
            => await AssembleAsync(ownerId, window, LocalDateOf(window, displayOffset), displayOffset, null, ct);

        /// <summary>
        /// Recap calendar path: projection, episode lookup and clipping all consume the same Analytics-verified
        /// instant window. The caller cannot substitute a correlation identity or rederive a fixed-duration end.
        /// </summary>
        public async Task<RecapProjectionResult> AssembleAsync(
            string ownerId, ResolvedCalendarWindow window, CancellationToken ct = default)
        {
            var instantWindow = InstantWindowOf(window);
            var localDate = DateOnlyOf(window.CivilStartDate);
            var displayOffset = DisplayOffsetAtStart(window);
            return await AssembleAsync(ownerId, instantWindow, localDate, displayOffset, window.TimeZone, ct);
        }

        private async Task<RecapProjectionResult> AssembleAsync(
            string ownerId, DateRange window, DateOnly localDate, TimeSpan displayOffset,
            string? displayTimeZone, CancellationToken ct)
        {
            var depthTables = await LoadDepthTablesAsync(ct);
            var segments = await QuerySegmentsAsync(ownerId, window.UtcStart, window.UtcEnd, ct);
            var strands = await LoadStrandsAsync(ownerId, ct);
            var episodes = await LoadEpisodesAsync(ownerId, localDate, ct);
            var recurring = await ComputeRecurringReadingsAsync(ownerId, window.UtcStart, depthTables, ct);
            return RecapProjection.Project(
                segments, window, displayOffset, strands, episodes, recurring, depthTables, displayTimeZone, localDate);
        }

        /// <summary>
        /// 该日当前应使用的知识投影标识（ADR-031 §7）：与 AssembleAsync 同一取数、同一纯函数，
        /// 确定性重算——历史读取判脏的比较端。只读，不调 LLM、不写缓存。
        /// </summary>
        public async Task<string> ComputeKnowledgeHashAsync(
            string ownerId, DateRange window, TimeSpan displayOffset, CancellationToken ct = default)
            => await ComputeKnowledgeHashAsync(
                ownerId, window, LocalDateOf(window, displayOffset), displayOffset, ct);

        public async Task<string> ComputeKnowledgeHashAsync(
            string ownerId, ResolvedCalendarWindow window, CancellationToken ct = default)
            => await ComputeKnowledgeHashAsync(
                ownerId,
                InstantWindowOf(window),
                DateOnlyOf(window.CivilStartDate),
                DisplayOffsetAtStart(window),
                ct);

        private async Task<string> ComputeKnowledgeHashAsync(
            string ownerId, DateRange window, DateOnly localDate, TimeSpan displayOffset, CancellationToken ct)
        {
            var depthTables = await LoadDepthTablesAsync(ct);
            var segments = await QuerySegmentsAsync(ownerId, window.UtcStart, window.UtcEnd, ct);
            var strands = await LoadStrandsAsync(ownerId, ct);
            var episodes = await LoadEpisodesAsync(ownerId, localDate, ct);
            return RecapProjection.ResolveKnowledge(
                segments, window, displayOffset, strands, episodes, depthTables, localDate).Hash;
        }

        private static DateOnly LocalDateOf(DateRange window, TimeSpan displayOffset)
            => DateOnly.FromDateTime(new DateTimeOffset(window.UtcStart, TimeSpan.Zero).ToOffset(displayOffset).Date);

        private static DateRange InstantWindowOf(ResolvedCalendarWindow window) =>
            new(window.Start.UtcDateTime, window.EndExclusive.UtcDateTime);

        private static DateOnly DateOnlyOf(LocalDate date) => new(date.Year, date.Month, date.Day);

        private static TimeSpan DisplayOffsetAtStart(ResolvedCalendarWindow window)
        {
            var zone = DateTimeZoneProviders.Tzdb[window.TimeZone];
            return zone.GetUtcOffset(Instant.FromDateTimeOffset(window.Start)).ToTimeSpan();
        }

        /// <summary>
        /// 生效深度表集（ADR-030 §4）：编译期种子作地板 + DB 声明按 max(Version) 覆盖
        /// （种子未跑 / 干净库也不失明）。表极小，每次装配现读——digest 装配本身低频（缓存判读挡在前面）。
        /// </summary>
        public async Task<DepthTables> LoadDepthTablesAsync(CancellationToken ct = default)
        {
            var payloads = await db.CollectorDeclarations
                .GroupBy(d => d.Source)
                .Select(g => g.OrderByDescending(d => d.Version).First().PayloadJson)
                .ToListAsync(ct);
            var declarations = payloads
                .Select(p => JsonSerializer.Deserialize<CollectorDeclarationDto>(p))
                .Where(d => d != null)
                .Select(d => d!);
            return new DepthTables(SeedDeclarations.All.Concat(declarations));
        }

        /// <summary>
        /// 窗口内是否有段（ADR-042 §2）：读路径判空用，便宜的存在性查询——GET 允许查库，
        /// 但不做完整装配、不调 LLM。
        /// </summary>
        public Task<bool> HasSegmentsAsync(
            string ownerId, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken ct = default)
            => db.ActivitySegments
                .Where(x => x.Device.OwnerId == ownerId)
                .Where(x => x.EndTime > windowStart && x.StartTime < windowEnd)
                .AnyAsync(ct);

        /// <summary>窗口内最新 segment 结束时间（裁剪到窗口终点）。今日缓存水位判读的比较端。</summary>
        public async Task<DateTimeOffset> LatestSegmentEndAsync(
            string ownerId, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken ct = default)
        {
            var latestEnd = await db.ActivitySegments
                .Where(x => x.Device.OwnerId == ownerId)
                .Where(x => x.EndTime > windowStart && x.StartTime < windowEnd)
                .MaxAsync(x => (DateTimeOffset?)x.EndTime, ct) ?? windowStart;
            return latestEnd > windowEnd ? windowEnd : latestEnd;
        }

        /// <summary>发问 few-shot 语境：绑定/静音裁决的渲染行（ADR-029 §4 裁决日志当锚）+ 判官读数词汇（ADR-030 §7）。</summary>
        public async Task<AskingContext> LoadAskingContextAsync(string ownerId, CancellationToken ct = default)
        {
            var strands = await db.Strands
                .Where(s => s.OwnerId == ownerId)
                .OrderByDescending(s => s.UpdatedAt)
                .Take(MaxExampleLines)
                .Select(s => new
                {
                    s.Name,
                    s.Gloss,
                    Members = s.Members.Select(m => new { m.Source, m.StepsJson }).ToList()
                })
                .ToListAsync(ct);
            var bound = strands
                .Select(s =>
                {
                    var fingerprint = string.Join("；", s.Members
                        .Select(m => MatcherRender.Describe(m.Source, MatcherCodec.Deserialize(m.StepsJson))));
                    return string.IsNullOrWhiteSpace(s.Gloss)
                        ? $"{s.Name} ← {fingerprint}"
                        : $"{s.Name}（{s.Gloss}）← {fingerprint}";
                })
                .ToList();

            var muted = await db.MutedMatchers
                .Where(m => m.OwnerId == ownerId)
                .OrderByDescending(m => m.CreatedAt)
                .Take(MaxExampleLines)
                .Select(m => new { m.Source, m.StepsJson })
                .ToListAsync(ct);
            var mutedLines = muted
                .Select(m => MatcherRender.Describe(m.Source, MatcherCodec.Deserialize(m.StepsJson)))
                .ToList();

            var depthTables = await LoadDepthTablesAsync(ct);
            return new AskingContext(bound, mutedLines, depthTables.DescribeForPrompt());
        }

        /// <summary>已裁决 Matcher 集（Strand 成员 ∪ Mute），按 (Source, 规范化 StepsJson) 比对——缓存问题的读时 diff 输入。</summary>
        public async Task<HashSet<(string Source, string StepsJson)>> LoadAdjudicatedAsync(
            string ownerId, CancellationToken ct = default)
        {
            var members = await db.StrandMatchers
                .Where(m => m.Strand.OwnerId == ownerId)
                .Select(m => new { m.Source, m.StepsJson })
                .ToListAsync(ct);
            var muted = await db.MutedMatchers
                .Where(m => m.OwnerId == ownerId)
                .Select(m => new { m.Source, m.StepsJson })
                .ToListAsync(ct);
            return members.Concat(muted).Select(x => (x.Source, x.StepsJson)).ToHashSet();
        }

        private async Task<List<RecapSegmentInput>> QuerySegmentsAsync(
            string ownerId, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken ct)
        {
            // 与投影同一套窗口规则：区间重叠，零长度点事件按落点归窗。
            return await db.ActivitySegments
                .Where(x => x.Device.OwnerId == ownerId)
                .Where(x => x.EndTime > windowStart && x.StartTime < windowEnd
                            || x.StartTime == x.EndTime && x.StartTime >= windowStart && x.StartTime < windowEnd)
                .Select(x => new RecapSegmentInput(
                    x.Device.DeviceName,
                    x.Source,
                    x.IdentityKey,
                    x.AppIdentityId != null
                        ? x.AppIdentity!.App.Key
                        : x.App != null ? x.App.Key : null,
                    x.Title,
                    x.StartTime,
                    x.EndTime,
                    x.Attributes))
                .ToListAsync(ct);
        }

        /// <summary>
        /// 载入该 Owner 的全部 Strand（树身份 + 叙事字段 + 有效日期 + 指纹），供日期知识投影
        /// （ADR-031 §7）：日期有效性过滤、命中解析、祖先链注入都在纯函数层（可测）。
        /// 机器世界知识不入库也不注入。
        /// </summary>
        private async Task<List<StrandKnowledgeInput>> LoadStrandsAsync(string ownerId, CancellationToken ct)
        {
            var strands = await db.Strands
                .Where(s => s.OwnerId == ownerId)
                .Select(s => new
                {
                    s.Id,
                    s.ParentStrandId,
                    s.Name,
                    s.Gloss,
                    s.StartedOn,
                    s.EndedOn,
                    Matchers = s.Members.Select(m => new { m.Source, m.StepsJson }).ToList()
                })
                .ToListAsync(ct);

            return strands
                .Select(s => new StrandKnowledgeInput(
                    s.Id, s.ParentStrandId, s.Name, s.Gloss, s.StartedOn, s.EndedOn,
                    s.Matchers
                        .Select(m => new MatcherDto { Source = m.Source, Steps = MatcherCodec.Deserialize(m.StepsJson) })
                        .ToList()))
                .ToList();
        }

        /// <summary>目标本地叙事日的 Episode（ADR-031 §7：只有 LocalDate 等于目标日期的进当日 Recap）。Probe 不取——不进 Recap prompt。</summary>
        private async Task<List<EpisodeKnowledgeInput>> LoadEpisodesAsync(
            string ownerId, DateOnly date, CancellationToken ct)
        {
            return await db.Episodes
                .Where(e => e.OwnerId == ownerId && e.LocalDate == date)
                .Select(e => new EpisodeKnowledgeInput(
                    e.Id, e.LocalDate, e.Text, e.ApproximateStart, e.ApproximateEnd, e.RelatedStrandId))
                .ToListAsync(ct);
        }

        /// <summary>
        /// 近 14 天高频根读数（ADR-029 §4 确定性注释，ADR-030 §7 声明驱动）：lookback 内出现
        /// ≥ RecurringMinDays 天的首层读数值。per-Source 标签分支已退役——browser 的"常驻"单位
        /// 随声明走（v1 = url，v2 提拔 site 后自动变站点）；离开合成段剔除。
        /// </summary>
        private async Task<IReadOnlyList<string>> ComputeRecurringReadingsAsync(
            string ownerId, DateTimeOffset windowStart, DepthTables depthTables, CancellationToken ct)
        {
            var from = windowStart.AddDays(-RecurringLookbackDays);
            var rows = await db.ActivitySegments
                .Where(x => x.Device.OwnerId == ownerId)
                .Where(x => x.EndTime > from && x.StartTime < windowStart)
                .Select(x => new
                {
                    x.Source,
                    AppName = x.AppIdentityId != null
                        ? x.AppIdentity!.App.Key
                        : x.App != null ? x.App.Key : null,
                    x.IdentityKey,
                    x.Title,
                    x.Attributes,
                    x.StartTime
                })
                .ToListAsync(ct);

            return rows
                .Select(r =>
                {
                    var readings = depthTables.ReadingsFor(r.Source, r.AppName, r.Title, r.IdentityKey, r.Attributes);
                    var root = readings.Count > 0 && readings[0].Layer == 1 ? readings[0].Value : null;
                    return (Label: root, Day: r.StartTime.UtcDateTime.Date);
                })
                .Where(t => t.Label != null && t.Label != SyntheticApps.Away)
                .Distinct()
                .GroupBy(t => t.Label!)
                .Where(g => g.Count() >= RecurringMinDays)
                .Select(g => g.Key)
                .OrderBy(l => l, StringComparer.Ordinal)
                .ToList();
        }
    }
}
