using Heartbeat.Core;
using Heartbeat.Core.DTOs.Knowledge;
using Heartbeat.Server.Data;
using Heartbeat.Server.Entities;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Services
{
    /// <summary>
    /// 提问器编排（ADR-028 §4 定稿）：确定性粗筛（QuestionProjection.Shortlist）→ LLM 分诊
    /// （ITriageGenerator，bind/mute 当 few-shot 锚）→ 只把 Ask 装进提问队列（封顶 3）。
    /// 分诊裁定按 (Owner, 把手) 缓存落库——刷看板不重复烧 token。
    /// </summary>
    public class QuestionService(AppDbContext db, ITriageGenerator? triage = null, TimeProvider? clock = null)
    {
        /// <summary>复现回看窗：把手在此窗内任一往日出现过即视为复现（ubiquity 压制的信号）。</summary>
        private const int RecurrenceLookbackDays = 14;

        /// <summary>每日提问封顶（ADR-028 §4）：不刷屏。</summary>
        private const int MaxQuestions = 3;

        /// <summary>few-shot 锚的条数上限（bind/mute 各自）：prompt 短、取最近的。</summary>
        private const int MaxAnchors = 10;

        private readonly AppDbContext _db = db;
        private readonly ITriageGenerator? _triage = triage;
        private readonly TimeProvider _clock = clock ?? TimeProvider.System;

        /// <summary>
        /// 当日提问队列：粗筛 → 缓存/分诊 → 只留 Ask → 封顶。分诊器缺席时全部退化为 Ask（不假装认识）。
        /// </summary>
        public async Task<DailyQuestionsResponse> GetDailyQuestionsAsync(
            string ownerId, DateTimeOffset date, CancellationToken ct = default)
        {
            var shortlist = await GetCandidatesAsync(ownerId, date, ct);
            var response = new DailyQuestionsResponse();
            if (shortlist.Count == 0) return response;

            var decisions = await ResolveDecisionsAsync(ownerId, shortlist, ct);

            foreach (var c in shortlist)
            {
                if (response.Questions.Count >= MaxQuestions) break;
                if (!decisions.TryGetValue(c.Handle, out var d) || d.Verdict != TriageVerdict.Ask) continue;

                response.Questions.Add(new QuestionItemResponse
                {
                    Anchor = new HandleDto { Source = c.Handle.Source, Token = c.Handle.Token },
                    Handles = c.CoOccurring.Select(h => new HandleDto { Source = h.Source, Token = h.Token }).ToList(),
                    TotalSeconds = c.TotalSeconds,
                    Start = c.Start,
                    End = c.End,
                    ProposedName = d.Name,
                    ProposedGloss = d.Gloss,
                });
            }
            return response;
        }

        /// <summary>
        /// 逐候选拿分诊裁定：缓存命中即回，未命中调 LLM 并落库（并发调用，DbContext 不进并发区）。
        /// 分诊器缺席时未命中一律 Ask 且不落库（等配置好再真判）。
        /// </summary>
        private async Task<Dictionary<HandleRef, TriageResult>> ResolveDecisionsAsync(
            string ownerId, IReadOnlyList<HandleCandidate> shortlist, CancellationToken ct)
        {
            var tokens = shortlist.Select(c => c.Handle.Token).Distinct().ToList();
            var cached = await _db.TriageDecisions
                .Where(t => t.OwnerId == ownerId && tokens.Contains(t.Token))
                .ToListAsync(ct);

            var result = new Dictionary<HandleRef, TriageResult>();
            foreach (var row in cached)
            {
                var handle = new HandleRef(row.Source, row.Token);
                if (shortlist.Any(c => c.Handle == handle))
                    result[handle] = new TriageResult(ParseVerdict(row.Verdict), row.Name, row.Gloss);
            }

            var missing = shortlist.Where(c => !result.ContainsKey(c.Handle)).ToList();
            if (missing.Count == 0) return result;

            if (_triage == null)
            {
                foreach (var c in missing) result[c.Handle] = TriageResult.FallbackAsk;
                return result;
            }

            var anchors = await LoadAnchorsAsync(ownerId, ct);

            // LLM 调用并发跑（纯 HTTP，不碰 DbContext）；落库在并发区之后串行。
            var triaged = await Task.WhenAll(missing.Select(async c =>
            {
                var input = new TriageInput(
                    c.Handle.Source, c.Handle.Token,
                    (int)Math.Round(c.TotalSeconds / 60),
                    c.CoOccurring.Select(h => $"{h.Source}/{h.Token}").ToList());
                var verdict = await _triage.TriageAsync(input, anchors, ct);
                return (c.Handle, verdict);
            }));

            var now = _clock.GetUtcNow();
            foreach (var (handle, verdict) in triaged)
            {
                result[handle] = verdict;
                _db.TriageDecisions.Add(new TriageDecision
                {
                    OwnerId = ownerId,
                    Source = handle.Source,
                    Token = handle.Token,
                    Verdict = FormatVerdict(verdict.Verdict),
                    Name = verdict.Name,
                    Gloss = verdict.Gloss,
                    DecidedAt = now,
                });
            }
            await _db.SaveChangesAsync(ct);
            return result;
        }

        /// <summary>裁决日志 → few-shot 锚：最近 bind 的把手（值得问的样子）/ 最近 mute 的（别问的样子）。</summary>
        private async Task<TriageAnchors> LoadAnchorsAsync(string ownerId, CancellationToken ct)
        {
            var bound = await _db.StrandHandles
                .Where(m => m.Strand.OwnerId == ownerId)
                .OrderByDescending(m => m.Id)
                .Take(MaxAnchors)
                .Select(m => m.Source + "/" + m.Token)
                .ToListAsync(ct);
            var muted = await _db.MutedHandles
                .Where(m => m.OwnerId == ownerId)
                .OrderByDescending(m => m.CreatedAt)
                .Take(MaxAnchors)
                .Select(m => m.Source + "/" + m.Token)
                .ToListAsync(ct);
            return new TriageAnchors(bound, muted);
        }

        private static TriageVerdict ParseVerdict(string s) => s switch
        {
            "known" => TriageVerdict.Known,
            "silent" => TriageVerdict.Silent,
            _ => TriageVerdict.Ask,
        };

        private static string FormatVerdict(TriageVerdict v) => v switch
        {
            TriageVerdict.Known => "known",
            TriageVerdict.Silent => "silent",
            _ => "ask",
        };

        /// <summary>确定性粗筛短名单（无 LLM）。测试与分诊共用的入口。</summary>
        public async Task<IReadOnlyList<HandleCandidate>> GetCandidatesAsync(
            string ownerId, DateTimeOffset date, CancellationToken ct = default)
        {
            var window = DateRange.Day(date);
            var windowStart = window.UtcStart;
            var windowEnd = window.UtcEnd;

            var intervals = new List<HandleInterval>();
            foreach (var s in await QueryRowsAsync(ownerId, windowStart, windowEnd, ct))
            {
                if (HandleDerivation.Derive(s.Source, s.AppName, s.Attributes, s.IdentityKey) is not { } h)
                    continue;
                var start = s.StartTime < windowStart ? windowStart : s.StartTime;
                var end = s.EndTime > windowEnd ? windowEnd : s.EndTime;
                if (end <= start) continue; // 裁剪后空段/点事件不进候选
                intervals.Add(new HandleInterval(h.Source, h.Token, start, end));
            }

            var adjudicated = await LoadAdjudicatedAsync(ownerId, ct);
            var recurring = await ComputeRecurringAsync(ownerId, windowStart, ct);

            return QuestionProjection.Shortlist(intervals, adjudicated, recurring);
        }

        private async Task<HashSet<HandleRef>> LoadAdjudicatedAsync(string ownerId, CancellationToken ct)
        {
            // 已绑定 Strand 的成员 ∪ 已 Mute 的把手：diff 靠这一集剔除。
            var bound = await _db.StrandHandles
                .Where(m => m.Strand.OwnerId == ownerId)
                .Select(m => new HandleRef(m.Source, m.Token))
                .ToListAsync(ct);
            var muted = await _db.MutedHandles
                .Where(m => m.OwnerId == ownerId)
                .Select(m => new HandleRef(m.Source, m.Token))
                .ToListAsync(ct);
            return [.. bound, .. muted];
        }

        private async Task<HashSet<HandleRef>> ComputeRecurringAsync(
            string ownerId, DateTimeOffset windowStart, CancellationToken ct)
        {
            var lookbackStart = windowStart.AddDays(-RecurrenceLookbackDays);
            var recurring = new HashSet<HandleRef>();
            foreach (var s in await QueryRowsAsync(ownerId, lookbackStart, windowStart, ct))
            {
                if (HandleDerivation.Derive(s.Source, s.AppName, s.Attributes, s.IdentityKey) is { } h)
                    recurring.Add(new HandleRef(h.Source, h.Token));
            }
            return recurring;
        }

        private Task<List<SegRow>> QueryRowsAsync(
            string ownerId, DateTimeOffset start, DateTimeOffset end, CancellationToken ct) =>
            _db.ActivitySegments
                .Where(x => x.Device.OwnerId == ownerId)
                .Where(x => x.EndTime > start && x.StartTime < end)
                .Select(x => new SegRow(
                    x.Source,
                    x.IdentityKey,
                    x.App != null ? x.App.Name : null,
                    x.Attributes,
                    x.StartTime,
                    x.EndTime))
                .ToListAsync(ct);

        private sealed record SegRow(
            string Source, string IdentityKey, string? AppName, string? Attributes,
            DateTimeOffset StartTime, DateTimeOffset EndTime);
    }
}
