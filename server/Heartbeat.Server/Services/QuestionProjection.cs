namespace Heartbeat.Server.Services
{
    /// <summary>(Source, Token) 把手引用。投影内的分组/集合键。</summary>
    public readonly record struct HandleRef(string Source, string Token);

    /// <summary>一段带把手的当日活动区间（已裁剪到日窗口）。QuestionProjection 的输入行。</summary>
    public readonly record struct HandleInterval(string Source, string Token, DateTimeOffset Start, DateTimeOffset End)
    {
        public HandleRef Handle => new(Source, Token);
        public double Seconds => (End - Start).TotalSeconds;
    }

    /// <summary>
    /// 一个候选提问簇（ADR-028 §4）：一簇共现把手 + 推断的锚点 + 未解释时长。
    /// 生成层（issue 03）据此拼提案；成员整簇随回答落库或被 Mute。
    /// </summary>
    public sealed class QuestionCluster
    {
        /// <summary>推断的锚点把手：命名提案的主体（recurring 优先，再按时长）。</summary>
        public required HandleRef Anchor { get; init; }

        /// <summary>簇内全部成员（含 Anchor）。</summary>
        public required IReadOnlyList<HandleRef> Handles { get; init; }

        /// <summary>簇的未解释时长（成员区间并集，避免重叠双计）。排序与 gate 用。</summary>
        public required double TotalSeconds { get; init; }

        public required DateTimeOffset Start { get; init; }
        public required DateTimeOffset End { get; init; }
    }

    /// <summary>
    /// 提问器投影（ADR-028 §4）：Recap 投影的第三出口。纯函数、无 I/O。
    /// 对 (当日把手区间, 已裁决把手, 复现把手) 做 diff → 时间贴邻聚簇 → gate → 封顶，
    /// 产出"该问哪些簇"。不含 LLM——提案 name/gloss 由生成层拼（issue 03）。
    /// </summary>
    public static class QuestionProjection
    {
        /// <summary>per-把手当日累计低于此值视为噪声，聚簇前剔除（复用 ADR-023 噪声地板）。</summary>
        private const int NoiseFloorSeconds = 60;

        /// <summary>时间贴邻阈值：区间间隔超过此值即断为不同注意力会话（游戏 vs 项目分帧）。</summary>
        private const int SessionGapSeconds = 600;

        /// <summary>单日"有意义时长"gate：达到即值得问（无需复现）。</summary>
        private const int MeaningfulSeconds = 1200;

        /// <summary>每日封顶提问数（ADR-028 §4）。</summary>
        private const int MaxQuestions = 3;

        public static IReadOnlyList<QuestionCluster> Project(
            IReadOnlyList<HandleInterval> intervals,
            IReadOnlySet<HandleRef> adjudicated,
            IReadOnlySet<HandleRef> recurring)
        {
            // 1. diff + 自锚优先：已绑定/已 Mute 的把手整体退出——既不被问，也不参与聚簇跟车。
            var live = intervals.Where(i => !adjudicated.Contains(i.Handle)).ToList();

            // 2. 噪声地板：per-把手累计 <60s 的把手在聚簇前剔除，免得一秒杂散把两会话桥接起来。
            var handleSeconds = live
                .GroupBy(i => i.Handle)
                .ToDictionary(g => g.Key, g => g.Sum(i => i.Seconds));
            var survivors = live
                .Where(i => handleSeconds[i.Handle] >= NoiseFloorSeconds)
                .OrderBy(i => i.Start)
                .ToList();
            if (survivors.Count == 0) return [];

            // 3. 时间贴邻会话聚簇：间隔 > SessionGap 即断会话；同会话内全部把手成一簇。
            var sessions = new List<List<HandleInterval>>();
            List<HandleInterval>? currentSession = null;
            DateTimeOffset runningEnd = default;
            foreach (var i in survivors)
            {
                if (currentSession == null || (i.Start - runningEnd).TotalSeconds > SessionGapSeconds)
                {
                    currentSession = [];
                    sessions.Add(currentSession);
                    runningEnd = i.End;
                }
                else if (i.End > runningEnd)
                {
                    runningEnd = i.End;
                }
                currentSession.Add(i);
            }

            // 4. 同一 constellation（把手集相同）跨会话合并为一簇，时长累加。
            var clusters = sessions
                .Select(BuildCluster)
                .GroupBy(c => new HandleSetKey(c.Handles))
                .Select(MergeSameConstellation)
                .ToList();

            // 5. gate：达到有意义时长，或含复现把手（每天短暂但反复的仪式）。
            var gated = clusters.Where(c =>
                c.TotalSeconds >= MeaningfulSeconds || c.Handles.Any(recurring.Contains));

            // 6. 按未解释时长封顶。
            return gated
                .OrderByDescending(c => c.TotalSeconds)
                .Take(MaxQuestions)
                .ToList();

            QuestionCluster BuildCluster(List<HandleInterval> session)
            {
                var handles = session.Select(i => i.Handle).Distinct().ToList();
                var anchor = handles
                    .OrderByDescending(h => recurring.Contains(h))          // 复现的更像稳定锚点
                    .ThenByDescending(h => handleSeconds[h])                 // 再按当日时长
                    .ThenBy(h => h.Token, StringComparer.Ordinal)           // 确定性兜底
                    .First();
                return new QuestionCluster
                {
                    Anchor = anchor,
                    Handles = handles,
                    TotalSeconds = UnionSeconds(session),
                    Start = session.Min(i => i.Start),
                    End = session.Max(i => i.End),
                };
            }
        }

        private static QuestionCluster MergeSameConstellation(IGrouping<HandleSetKey, QuestionCluster> group)
        {
            if (group.Count() == 1) return group.First();
            var first = group.First();
            return new QuestionCluster
            {
                Anchor = first.Anchor,
                Handles = first.Handles,
                TotalSeconds = group.Sum(c => c.TotalSeconds),
                Start = group.Min(c => c.Start),
                End = group.Max(c => c.End),
            };
        }

        /// <summary>区间并集时长：簇内重叠区间不双计。</summary>
        private static double UnionSeconds(List<HandleInterval> session)
        {
            var ordered = session.OrderBy(i => i.Start).ToList();
            double total = 0;
            DateTimeOffset cursor = DateTimeOffset.MinValue;
            foreach (var i in ordered)
            {
                var start = i.Start > cursor ? i.Start : cursor;
                if (i.End > start) total += (i.End - start).TotalSeconds;
                if (i.End > cursor) cursor = i.End;
            }
            return total;
        }

        /// <summary>把手集的顺序无关相等键（同 constellation 判定）。</summary>
        private readonly struct HandleSetKey : IEquatable<HandleSetKey>
        {
            private readonly HashSet<HandleRef> _set;
            public HandleSetKey(IReadOnlyList<HandleRef> handles) => _set = [.. handles];
            public bool Equals(HandleSetKey other) => _set.SetEquals(other._set);
            public override bool Equals(object? obj) => obj is HandleSetKey k && Equals(k);
            public override int GetHashCode()
            {
                var hash = 0;
                foreach (var h in _set) hash ^= h.GetHashCode(); // 顺序无关
                return hash;
            }
        }
    }
}
