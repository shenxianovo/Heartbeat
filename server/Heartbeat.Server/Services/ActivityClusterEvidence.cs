using Heartbeat.Core;
using Heartbeat.Core.DTOs.Knowledge;

namespace Heartbeat.Server.Services
{
    /// <summary>
    /// ActivityCluster 证据物化（ADR-031 §1/§6，纯函数）：判官只提名"问什么 + 锚定谓词"，
    /// 证据卡由这里从真实 segments 确定性生成——大概时段、跨 Source 观察、可读标题都来自
    /// 实际观测，不把模型推断冒充事实。瞬时视图：不持久化实体、不写 Segment 归属。
    /// </summary>
    public static class ActivityClusterEvidence
    {
        /// <summary>证据卡上跨 Source 观察行的封顶（指纹命中行优先，旁证按时长补足）。</summary>
        private const int MaxObservations = 8;

        /// <summary>
        /// 物化一张证据卡：谓词在窗口内命中的 segments 决定大概时段，时段内全部真实活动
        /// 构成跨 Source 观察。谓词在当日证据上零命中返回 null——判官引用了不存在的活动，
        /// 该问题整个丢弃（高精度纪律：不发无证据可核对的问题）。
        /// </summary>
        public static AskingQuestionResponse? Materialize(
            Guid id,
            string kind,
            string question,
            MatcherDto matcher,
            IReadOnlyList<RecapSegmentInput> segments,
            DateRange window,
            DepthTables depthTables)
        {
            DateTimeOffset windowStart = window.UtcStart;
            DateTimeOffset windowEnd = window.UtcEnd;

            // 与 Recap 投影同一窗口规则：区间重叠 + 裁剪，零长度点事件按落点归窗。
            var clipped = segments
                .Where(s => s.EndTime > windowStart && s.StartTime < windowEnd
                            || s.StartTime == s.EndTime && s.StartTime >= windowStart && s.StartTime < windowEnd)
                .Select(s =>
                {
                    var readings = depthTables.ReadingsFor(s.Source, s.AppName, s.Title, s.IdentityKey, s.AttributesJson);
                    return new Row(
                        s,
                        s.StartTime < windowStart ? windowStart : s.StartTime,
                        s.EndTime > windowEnd ? windowEnd : s.EndTime,
                        readings,
                        MatcherEval.Hits(s.Source, readings, matcher));
                })
                .ToList();

            var hits = clipped.Where(r => r.Matches).ToList();
            if (hits.Count == 0) return null;

            var spanStart = hits.Min(r => r.Start);
            var spanEnd = hits.Max(r => r.End);

            // 时段内的跨 Source 观察：按 (Source, 最浅读数值) 聚合，时长裁剪到证据时段。
            var groups = clipped
                .Where(r => r.End > spanStart && r.Start < spanEnd || r.Start == r.End)
                .Where(r => r.Readings.Count > 0)
                .Select(r => r with
                {
                    Start = r.Start < spanStart ? spanStart : r.Start,
                    End = r.End > spanEnd ? spanEnd : r.End,
                })
                .Where(r => r.Readings[0].Value != SyntheticApps.Away)
                .GroupBy(r => (r.Segment.Source, Root: r.Readings[0].Value))
                .Select(g => new EvidenceObservationDto
                {
                    Source = g.Key.Source,
                    Value = g.Key.Root,
                    Detail = g
                        .Select(r => r.Readings.FirstOrDefault(x => x.Layer > r.Readings[0].Layer))
                        .Where(x => x != default)
                        .GroupBy(x => x.Value)
                        .OrderByDescending(d => d.Count())
                        .Select(d => d.Key)
                        .FirstOrDefault(),
                    Seconds = g.Sum(r => (r.End - r.Start).TotalSeconds),
                    MatchesFingerprint = g.Any(r => r.Matches),
                })
                .OrderByDescending(o => o.MatchesFingerprint)
                .ThenByDescending(o => o.Seconds)
                .ThenBy(o => o.Value, StringComparer.Ordinal)
                .Take(MaxObservations)
                .ToList();

            return new AskingQuestionResponse
            {
                Id = id,
                Kind = kind,
                Question = question,
                Matcher = matcher,
                ApproximateStart = spanStart,
                ApproximateEnd = spanEnd,
                Observations = groups,
            };
        }

        private sealed record Row(
            RecapSegmentInput Segment,
            DateTimeOffset Start,
            DateTimeOffset End,
            IReadOnlyList<DepthReading> Readings,
            bool Matches);
    }
}
