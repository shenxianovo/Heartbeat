using System.Text;
using Heartbeat.Core;

namespace Heartbeat.Server.Services
{
    /// <summary>
    /// Recap 投影的输入行：一条已物化的 segment 及其关联显示名。
    /// Attributes 有意不进投影——browser 的 IdentityKey 已是规范化 URL，Title 已是页面标题，
    /// 自由结构 JSON 只会稀释 token（ADR-023 §3）。
    /// </summary>
    public record RecapSegmentInput(
        string DeviceName,
        string Source,
        string IdentityKey,
        string? AppName,
        string? Title,
        DateTimeOffset StartTime,
        DateTimeOffset EndTime);

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
    /// Recap 投影（ADR-023 §2/§3）：segments → LLM 输入摘要的确定性压缩。
    /// 纯函数、无 I/O——Recap 质量的可测核心，也是未来外部 Agent 能力暴露的开门处。
    /// 双轨模型：system 段按设备分轨作注意力骨架（轨内互斥），插件段按 IdentityKey 聚合作语义细节。
    /// 压缩只影响本投影，不动数据层。
    /// </summary>
    public static class RecapProjection
    {
        /// <summary>同 App 相邻 system 段的合并容差（快照节律与瞬时切换产生的缝）。</summary>
        private const int MergeGapSeconds = 120;

        /// <summary>低于此时长的注意力块视为噪声丢弃（只丢时间轴行，应用时长统计仍如实累计）。</summary>
        private const int NoiseBlockSeconds = 60;

        private const int MaxTitlesPerBlock = 2;
        private const int MaxAppsPerDevice = 8;
        private const int MaxPluginEntriesPerSource = 30;

        public static RecapProjectionResult Project(
            IReadOnlyList<RecapSegmentInput> segments,
            DateRange window,
            TimeSpan displayOffset)
        {
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
                AppendSystemTrack(sb, device.ToList(), windowEnd, displayOffset);
                AppendPluginTracks(sb, device.ToList());
            }

            return new RecapProjectionResult
            {
                IsEmpty = false,
                Digest = sb.ToString(),
                SegmentWatermarkUtc = watermark
            };
        }

        private sealed record ClippedSegment(RecapSegmentInput Segment, DateTimeOffset Start, DateTimeOffset End)
        {
            public double Seconds => (End - Start).TotalSeconds;
        }

        /// <summary>注意力块：同 App 相邻 system 段折叠后的时间轴行。</summary>
        private sealed class AttentionBlock
        {
            public required string App { get; init; }
            public DateTimeOffset Start { get; init; }
            public DateTimeOffset End { get; set; }
            public Dictionary<string, double> TitleSeconds { get; } = [];

            public double Seconds => (End - Start).TotalSeconds;

            public void Absorb(ClippedSegment c)
            {
                if (c.End > End) End = c.End;
                if (string.IsNullOrWhiteSpace(c.Segment.Title)) return;
                TitleSeconds.TryGetValue(c.Segment.Title, out var acc);
                TitleSeconds[c.Segment.Title] = acc + c.Seconds;
            }
        }

        private static void AppendSystemTrack(
            StringBuilder sb, List<ClippedSegment> deviceSegments, DateTimeOffset windowEnd, TimeSpan displayOffset)
        {
            var system = deviceSegments
                .Where(c => c.Segment.Source == ActivitySources.System)
                .OrderBy(c => c.Start)
                .ToList();
            if (system.Count == 0) return;

            var blocks = new List<AttentionBlock>();
            AttentionBlock? current = null;
            foreach (var c in system)
            {
                var app = string.IsNullOrWhiteSpace(c.Segment.AppName) ? "(unknown)" : c.Segment.AppName;
                if (current != null && current.App == app
                    && (c.Start - current.End).TotalSeconds <= MergeGapSeconds)
                {
                    current.Absorb(c);
                    continue;
                }
                current = new AttentionBlock { App = app, Start = c.Start, End = c.Start };
                current.Absorb(c);
                blocks.Add(current);
            }

            sb.AppendLine("注意力轨（前台互斥，时长可信；不与其他设备求和）：");
            foreach (var b in blocks.Where(b => b.Seconds >= NoiseBlockSeconds))
            {
                var label = b.App == SyntheticApps.Away ? "离开" : b.App;
                var titles = b.App == SyntheticApps.Away
                    ? []
                    : b.TitleSeconds.OrderByDescending(t => t.Value).Take(MaxTitlesPerBlock).Select(t => t.Key).ToList();
                var titlePart = titles.Count > 0 ? $"｜窗口: {string.Join("; ", titles)}" : string.Empty;
                sb.AppendLine($"- {FormatTime(b.Start, windowEnd, displayOffset)}–{FormatTime(b.End, windowEnd, displayOffset)} {label}（{FormatDuration(b.Seconds)}）{titlePart}");
            }

            var totals = system
                .GroupBy(c => string.IsNullOrWhiteSpace(c.Segment.AppName) ? "(unknown)" : c.Segment.AppName)
                .Select(g => (App: g.Key, Seconds: g.Sum(c => c.Seconds)))
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

        private static void AppendPluginTracks(StringBuilder sb, List<ClippedSegment> deviceSegments)
        {
            var bySource = deviceSegments
                .Where(c => c.Segment.Source != ActivitySources.System)
                .GroupBy(c => c.Segment.Source)
                .OrderBy(g => g.Key, StringComparer.Ordinal);

            foreach (var source in bySource)
            {
                var entries = source
                    .GroupBy(c => c.Segment.IdentityKey)
                    .Select(g =>
                    {
                        var longest = g.OrderByDescending(c => c.Seconds).First();
                        return (
                            IdentityKey: g.Key,
                            Title: g.Where(c => !string.IsNullOrWhiteSpace(c.Segment.Title))
                                    .OrderByDescending(c => c.Seconds)
                                    .Select(c => c.Segment.Title)
                                    .FirstOrDefault(),
                            Seconds: g.Sum(c => c.Seconds),
                            Visits: g.Count());
                    })
                    .OrderByDescending(e => e.Seconds)
                    .ThenByDescending(e => e.Visits)
                    .ToList();

                sb.AppendLine($"语义细节轨 [{source.Key}]（与注意力轨重叠为正常，时长不与上轨相加）：");
                foreach (var e in entries.Take(MaxPluginEntriesPerSource))
                {
                    var display = e.Title != null && e.Title != e.IdentityKey
                        ? $"{e.Title}（{e.IdentityKey}）"
                        : e.IdentityKey;
                    sb.AppendLine($"- {display} — 合计 {FormatDuration(e.Seconds)}，{e.Visits} 次");
                }
                if (entries.Count > MaxPluginEntriesPerSource)
                    sb.AppendLine($"（另有 {entries.Count - MaxPluginEntriesPerSource} 条较短的记录未列出）");
            }
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
