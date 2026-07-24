using System.Text.Json;
using Heartbeat.Core;
using Heartbeat.Core.DTOs.Collectors;
using Heartbeat.Core.DTOs.Knowledge;
using Heartbeat.Server.Data;
using Microsoft.EntityFrameworkCore;

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
        {
            var depthTables = await LoadDepthTablesAsync(ct);
            var segments = await QuerySegmentsAsync(ownerId, window.UtcStart, window.UtcEnd, ct);
            var knownStrands = await LoadKnownStrandsAsync(ownerId, ct);
            var recurring = await ComputeRecurringReadingsAsync(ownerId, window.UtcStart, depthTables, ct);
            return RecapProjection.Project(segments, window, displayOffset, knownStrands, recurring, depthTables);
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
                    x.App != null ? x.App.Name : null,
                    x.Title,
                    x.StartTime,
                    x.EndTime,
                    x.Attributes))
                .ToListAsync(ct);
        }

        /// <summary>
        /// 载入该 Owner 的全部 Strand（名字 + 释义 + Matcher 指纹），供投影反哺（ADR-029 §1/§3）：
        /// 注入只在指纹当日命中时发生，命中判断在投影层（可测）。机器世界知识不入库也不注入。
        /// </summary>
        private async Task<List<KnownStrandInput>> LoadKnownStrandsAsync(string ownerId, CancellationToken ct)
        {
            var strands = await db.Strands
                .Where(s => s.OwnerId == ownerId)
                .Select(s => new
                {
                    s.Name,
                    s.Gloss,
                    Matchers = s.Members.Select(m => new { m.Source, m.StepsJson }).ToList()
                })
                .ToListAsync(ct);

            return strands
                .Select(s => new KnownStrandInput(
                    s.Name,
                    s.Gloss,
                    s.Matchers
                        .Select(m => new MatcherDto { Source = m.Source, Steps = MatcherCodec.Deserialize(m.StepsJson) })
                        .ToList()))
                .ToList();
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
                    AppName = x.App != null ? x.App.Name : null,
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
