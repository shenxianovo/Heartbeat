using Heartbeat.Core;
using Heartbeat.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Services
{
    /// <summary>
    /// 提问器的 DB 装配（ADR-028 §4）：查当日段派生把手区间、载已裁决把手与复现把手，
    /// 喂给纯投影 QuestionProjection。无编排逻辑——策略在投影里，可测。端点属 issue 03。
    /// </summary>
    public class QuestionService(AppDbContext db)
    {
        /// <summary>复现回看窗：把手在此窗内任一往日出现过即视为复现（ADR-028 §4 的复现信号）。</summary>
        private const int RecurrenceLookbackDays = 14;

        private readonly AppDbContext _db = db;

        public async Task<IReadOnlyList<QuestionCluster>> GetCandidatesAsync(
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
                if (end <= start) continue; // 裁剪后空段/点事件不进聚簇
                intervals.Add(new HandleInterval(h.Source, h.Token, start, end));
            }

            var adjudicated = await LoadAdjudicatedAsync(ownerId, ct);
            var recurring = await ComputeRecurringAsync(ownerId, windowStart, ct);

            return QuestionProjection.Project(intervals, adjudicated, recurring);
        }

        private async Task<HashSet<HandleRef>> LoadAdjudicatedAsync(string ownerId, CancellationToken ct)
        {
            // 已绑定 Strand 的成员 ∪ 已 Mute 的把手：diff + 自锚优先都靠这一集剔除。
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
