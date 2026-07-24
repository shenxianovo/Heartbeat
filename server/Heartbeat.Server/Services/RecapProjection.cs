using System.Text;
using Heartbeat.Core;

namespace Heartbeat.Server.Services
{
    /// <summary>
    /// Recap 投影的输入行：一条已物化的 segment 及其关联显示名。
    /// AttributesJson 只供声明的 attributes.* 槽位取读数（ADR-030 §2），不做自由结构消费。
    /// </summary>
    public record RecapSegmentInput(
        string DeviceName,
        string Source,
        string IdentityKey,
        string? AppName,
        string? Title,
        DateTimeOffset StartTime,
        DateTimeOffset EndTime,
        string? AttributesJson = null);

    public class RecapProjectionResult
    {
        /// <summary>窗口内零 segment。空日不调 LLM（ADR-023 §5）。</summary>
        public required bool IsEmpty { get; init; }

        /// <summary>LLM 输入的活动摘要文本。口吻指令不在此处——投影只产数据，产品人格属生成层。</summary>
        public required string Digest { get; init; }

        /// <summary>本次投影消费到的最新 segment 时间（UTC，裁剪到窗口）。今日缓存的新鲜度水位（ADR-023 §4）。空日为窗口起点。</summary>
        public required DateTime SegmentWatermarkUtc { get; init; }
    }

    /// <summary>
    /// Recap 投影（ADR-023 §2/§3，ADR-029 §2）：segments → LLM 输入摘要的确定性压缩。
    /// 纯函数、无 I/O——Recap 质量的可测核心，叙事与发问两次调用共用同一 digest。
    /// 双轨模型：system 段按设备分轨作注意力骨架（轨内互斥），插件段按 IdentityKey 聚合作语义细节。
    /// 身份维度按观测深度长成深度树：块 = L1 读数聚合，块内挂下一深度分解（预算剪枝）。
    /// 压缩只影响本投影，不动数据层。
    /// </summary>
    public static class RecapProjection
    {
        /// <summary>同 App 相邻 system 段的合并容差（快照节律与瞬时切换产生的缝）。</summary>
        private const int MergeGapSeconds = 120;

        /// <summary>低于此时长的注意力块视为噪声丢弃（只丢时间轴行，应用时长统计仍如实累计）。</summary>
        private const int NoiseBlockSeconds = 60;

        /// <summary>深度树预算（ADR-029 §2）：块时长达到此值才展开下一深度分解。</summary>
        private const int BreakdownExpandSeconds = 600;

        /// <summary>深度树预算：展开块的分解条目封顶，尾部折叠成"其他 N 个"。</summary>
        private const int MaxBreakdownEntries = 4;

        private const int MaxAppsPerDevice = 8;
        private const int MaxPluginEntriesPerSource = 30;

        public static RecapProjectionResult Project(
            IReadOnlyList<RecapSegmentInput> segments,
            DateRange window,
            TimeSpan displayOffset,
            IReadOnlyList<KnownStrandInput>? knownStrands = null,
            IReadOnlyList<string>? recurringReadings = null,
            DepthTables? depthTables = null)
        {
            // 生效声明由调用方从库取（DigestAssembler）；纯函数测试与 bootstrap 回落种子副本。
            depthTables ??= DepthTables.Seeds;
            DateTimeOffset windowStart = window.UtcStart;
            DateTimeOffset windowEnd = window.UtcEnd;

            // 区间重叠 + 裁剪，与报表同一规则（ADR-018 §4）。零长度点事件（Start == End）在窗口内也保留。
            var clipped = segments
                .Where(s => s.EndTime > windowStart && s.StartTime < windowEnd
                            || s.StartTime == s.EndTime && s.StartTime >= windowStart && s.StartTime < windowEnd)
                .Select(s => new ClippedSegment(
                    s,
                    s.StartTime < windowStart ? windowStart : s.StartTime,
                    s.EndTime > windowEnd ? windowEnd : s.EndTime))
                .OrderBy(c => c.Start)
                .ToList();

            if (clipped.Count == 0)
            {
                return new RecapProjectionResult
                {
                    IsEmpty = true,
                    Digest = string.Empty,
                    SegmentWatermarkUtc = window.UtcStart
                };
            }

            var watermark = clipped.Max(c => c.End).UtcDateTime;

            var sb = new StringBuilder();
            var localDate = windowStart.ToOffset(displayOffset);
            sb.AppendLine($"# 活动摘要 {localDate:yyyy-MM-dd}（{FormatOffset(displayOffset)}）");

            var deviceGroups = clipped
                .GroupBy(c => c.Segment.DeviceName)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .ToList();

            sb.AppendLine($"设备：{string.Join("、", deviceGroups.Select(g => g.Key))}");

            foreach (var device in deviceGroups)
            {
                sb.AppendLine();
                sb.AppendLine($"## 设备「{device.Key}」");
                AppendSystemTrack(sb, device.ToList(), windowEnd, displayOffset, depthTables);
                AppendPluginTracks(sb, device.ToList(), depthTables);
            }

            AppendKnownStrands(sb, clipped, knownStrands, depthTables);
            AppendRecurringNote(sb, recurringReadings);

            return new RecapProjectionResult
            {
                IsEmpty = false,
                Digest = sb.ToString(),
                SegmentWatermarkUtc = watermark
            };
        }

        /// <summary>
        /// 已知脉络块（ADR-028 §6，解析随 ADR-029 换 Matcher 命中）：注入只在指纹当日命中时发生——
        /// 把当日观测归到用户确认过的 Strand，让生成层用项目名而非 app 名叙事。
        /// 解析确定性（声明驱动的读数提取 + MatcherEval），留在可测的投影层。
        /// </summary>
        private static void AppendKnownStrands(
            StringBuilder sb, List<ClippedSegment> clipped, IReadOnlyList<KnownStrandInput>? knownStrands,
            DepthTables depthTables)
        {
            if (knownStrands == null || knownStrands.Count == 0) return;

            var observed = clipped
                .Select(c => (c.Segment.Source, Readings: depthTables.ReadingsFor(
                    c.Segment.Source, c.Segment.AppName, c.Segment.Title, c.Segment.IdentityKey,
                    c.Segment.AttributesJson)))
                .ToList();

            var present = knownStrands
                .Where(s => s.Matchers.Any(m => observed.Any(o => MatcherEval.Hits(o.Source, o.Readings, m))))
                .DistinctBy(s => s.Name)
                .OrderBy(s => s.Name, StringComparer.Ordinal)
                .ToList();
            if (present.Count == 0) return;

            sb.AppendLine();
            sb.AppendLine("## 已知脉络（把观测归到你确认过的项目；用这些名字称呼对应活动）");
            foreach (var g in present)
                sb.AppendLine(string.IsNullOrWhiteSpace(g.Gloss) ? $"- {g.Name}" : $"- {g.Name}：{g.Gloss}");
        }

        private sealed record ClippedSegment(RecapSegmentInput Segment, DateTimeOffset Start, DateTimeOffset End)
        {
            public double Seconds => (End - Start).TotalSeconds;
        }

        /// <summary>
        /// 深度树节点（ADR-030 §7）：某读数值下的并集时长 + 访问次数 + 下一深度分解。
        /// 缺更深读数的段挂在当前节点（最深可用读数），不造假值。
        /// </summary>
        private sealed class DepthNode
        {
            public double Seconds;
            public int Visits;
            public Dictionary<string, DepthNode> Children { get; } = [];
        }

        /// <summary>把段插进深度树：按声明层序取每层首读数（分解轴）的值为路径。</summary>
        private static void Insert(Dictionary<string, DepthNode> roots, IReadOnlyList<DepthReading> readings, ClippedSegment c)
        {
            var level = roots;
            DepthNode? node = null;
            var lastLayer = 0;
            foreach (var r in readings)
            {
                if (r.Layer <= lastLayer) continue; // 层内非首读数不是分解轴
                lastLayer = r.Layer;
                if (!level.TryGetValue(r.Value, out var next))
                    level[r.Value] = next = new DepthNode();
                node = next;
                node.Seconds += c.Seconds;
                node.Visits += 1;
                level = node.Children;
            }
        }

        /// <summary>注意力块：同根读数值相邻 system 段折叠后的时间轴行，块内挂下一深度分解树。</summary>
        private sealed class AttentionBlock
        {
            public required string App { get; init; }
            public DateTimeOffset Start { get; init; }
            public DateTimeOffset End { get; set; }
            public Dictionary<string, DepthNode> Breakdown { get; } = [];

            public double Seconds => (End - Start).TotalSeconds;

            public void Absorb(ClippedSegment c, IReadOnlyList<DepthReading> readings)
            {
                if (c.End > End) End = c.End;
                // 块轴 = 根读数；分解树从第二层起（根之下的读数路径）。
                Insert(Breakdown, readings.Where(r => r.Layer > 1).ToList(), c);
            }
        }

        private static void AppendSystemTrack(
            StringBuilder sb, List<ClippedSegment> deviceSegments, DateTimeOffset windowEnd, TimeSpan displayOffset,
            DepthTables depthTables)
        {
            var system = deviceSegments
                .Where(c => c.Segment.Source == ActivitySources.System)
                .OrderBy(c => c.Start)
                .Select(c =>
                {
                    var readings = depthTables.ReadingsFor(
                        c.Segment.Source, c.Segment.AppName, c.Segment.Title, c.Segment.IdentityKey,
                        c.Segment.AttributesJson);
                    // 根轴缺值的展示回落在轨渲染层（解释器不造假值）：段不从时间轴消失。
                    var root = readings.Count > 0 && readings[0].Layer == 1
                        ? readings[0].Value
                        : DepthTables.UnknownValue;
                    return (Clipped: c, Root: root, Readings: readings);
                })
                .ToList();
            if (system.Count == 0) return;

            var blocks = new List<AttentionBlock>();
            AttentionBlock? current = null;
            foreach (var (c, root, readings) in system)
            {
                if (current != null && current.App == root
                    && (c.Start - current.End).TotalSeconds <= MergeGapSeconds)
                {
                    current.Absorb(c, readings);
                    continue;
                }
                current = new AttentionBlock { App = root, Start = c.Start, End = c.Start };
                current.Absorb(c, readings);
                blocks.Add(current);
            }

            sb.AppendLine("注意力轨（前台互斥，时长可信；不与其他设备求和）：");
            foreach (var b in blocks.Where(b => b.Seconds >= NoiseBlockSeconds))
            {
                var label = b.App == SyntheticApps.Away ? "离开" : b.App;
                var breakdown = b.App == SyntheticApps.Away ? string.Empty : FormatBreakdown(b.Breakdown, b.Seconds);
                sb.AppendLine($"- {FormatTime(b.Start, windowEnd, displayOffset)}–{FormatTime(b.End, windowEnd, displayOffset)} {label}（{FormatDuration(b.Seconds)}）{breakdown}");
            }

            var totals = system
                .GroupBy(t => t.Root)
                .Select(g => (App: g.Key, Seconds: g.Sum(t => t.Clipped.Seconds)))
                .ToList();
            var ranked = totals
                .Where(t => t.App != SyntheticApps.Away)
                .OrderByDescending(t => t.Seconds)
                .Take(MaxAppsPerDevice)
                .ToList();
            if (ranked.Count > 0)
                sb.AppendLine($"应用时长：{string.Join(" · ", ranked.Select(t => $"{t.App} {FormatDuration(t.Seconds)}"))}");

            var awaySeconds = totals.Where(t => t.App == SyntheticApps.Away).Sum(t => t.Seconds);
            if (awaySeconds >= NoiseBlockSeconds)
                sb.AppendLine($"离开合计：{FormatDuration(awaySeconds)}");
        }

        private static void AppendPluginTracks(
            StringBuilder sb, List<ClippedSegment> deviceSegments, DepthTables depthTables)
        {
            var bySource = deviceSegments
                .Where(c => c.Segment.Source != ActivitySources.System)
                .GroupBy(c => c.Segment.Source)
                .OrderBy(g => g.Key, StringComparer.Ordinal);

            foreach (var source in bySource)
            {
                // 声明驱动的深度树（ADR-030 §7）：browser 由 url → tab_title 两层（v2 后 site→url→tab_title），
                // 树根即最浅读数。轨内非互斥，节点时长为并集累计、次数为出现段数。
                var roots = new Dictionary<string, DepthNode>();
                foreach (var c in source)
                {
                    var readings = depthTables.ReadingsFor(
                        c.Segment.Source, c.Segment.AppName, c.Segment.Title, c.Segment.IdentityKey, c.Segment.AttributesJson);
                    Insert(roots, readings, c);
                }

                var entries = roots
                    .OrderByDescending(e => e.Value.Seconds)
                    .ThenByDescending(e => e.Value.Visits)
                    .ToList();

                sb.AppendLine($"语义细节轨 [{source.Key}]（与注意力轨重叠为正常，时长不与上轨相加）：");
                foreach (var (value, node) in entries.Take(MaxPluginEntriesPerSource))
                    sb.AppendLine($"- {value} — 合计 {FormatDuration(node.Seconds)}，{node.Visits} 次{FormatBreakdown(node.Children, node.Seconds)}");
                if (entries.Count > MaxPluginEntriesPerSource)
                    sb.AppendLine($"（另有 {entries.Count - MaxPluginEntriesPerSource} 条较短的记录未列出）");
            }
        }

        /// <summary>
        /// 深度树某节点的下一深度分解（ADR-030 §7）：子读数值的去重分布，按时长降序。
        /// 预算剪枝：父时长未达展开门槛只给头名，子数封顶，尾部折叠"其他 N 个"。
        /// 递归：更深读数继续下探（browser v2 的 url 下 tab_title）。
        /// </summary>
        private static string FormatBreakdown(Dictionary<string, DepthNode> level, double parentSeconds)
        {
            if (level.Count == 0) return string.Empty;

            var ordered = level
                .OrderByDescending(t => t.Value.Seconds)
                .ThenBy(t => t.Key, StringComparer.Ordinal)
                .ToList();
            var cap = parentSeconds >= BreakdownExpandSeconds ? MaxBreakdownEntries : 1;

            var shown = ordered.Take(cap)
                .Select(t => $"{t.Key} {FormatDuration(t.Value.Seconds)}{FormatBreakdown(t.Value.Children, t.Value.Seconds)}")
                .ToList();
            if (ordered.Count > cap)
            {
                var rest = ordered.Skip(cap).ToList();
                shown.Add($"其他 {rest.Count} 个 {FormatDuration(rest.Sum(t => t.Value.Seconds))}");
            }
            return $"｜其中: {string.Join(" · ", shown)}";
        }

        /// <summary>近 14 天高频读数注释（ADR-029 §4 确定性注释）：输入由 service 提供，投影只渲染。</summary>
        private static void AppendRecurringNote(StringBuilder sb, IReadOnlyList<string>? recurringReadings)
        {
            if (recurringReadings == null || recurringReadings.Count == 0) return;
            sb.AppendLine();
            sb.AppendLine($"近 14 天高频出现（无处不在的基础设施，不是「在做的事」的证据）：{string.Join("、", recurringReadings)}");
        }

        private static string FormatTime(DateTimeOffset t, DateTimeOffset windowEnd, TimeSpan displayOffset)
            => t == windowEnd ? "24:00" : t.ToOffset(displayOffset).ToString("HH:mm");

        private static string FormatDuration(double seconds)
        {
            if (seconds < 60) return "<1分";
            var t = TimeSpan.FromSeconds(seconds);
            return t.TotalMinutes < 60 ? $"{(int)t.TotalMinutes}分" : $"{(int)t.TotalHours}小时{t.Minutes:D2}分";
        }

        private static string FormatOffset(TimeSpan offset)
        {
            var sign = offset < TimeSpan.Zero ? "-" : "+";
            return $"UTC{sign}{offset.Duration():hh\\:mm}";
        }
    }
}
