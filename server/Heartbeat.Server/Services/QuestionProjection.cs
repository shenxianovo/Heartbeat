using Heartbeat.Core;

namespace Heartbeat.Server.Services
{
    /// <summary>(Source, Token) 把手引用。投影内的分组/集合键。</summary>
    public readonly record struct HandleRef(string Source, string Token);

    /// <summary>把手所属 Strand 的展示信息（Recap 反哺注入用，ADR-028 §6）。</summary>
    public readonly record struct StrandGloss(string Name, string Gloss);

    /// <summary>一段带把手的当日活动区间（已裁剪到日窗口）。QuestionProjection 的输入行。</summary>
    public readonly record struct HandleInterval(string Source, string Token, DateTimeOffset Start, DateTimeOffset End)
    {
        public HandleRef Handle => new(Source, Token);
        public double Seconds => (End - Start).TotalSeconds;
    }

    /// <summary>
    /// 一个候选提问（ADR-028 §4，单锚点重构）：针对**一个**高特异性把手发问，而非一簇。
    /// 40 项勾选框没有信息量——"这一个是什么"才是用户当初要的"问花生是什么"。
    /// </summary>
    public sealed class QuestionCandidate
    {
        /// <summary>被问的把手：命名提案的主体，也是回答落库时 Strand 的种子成员。</summary>
        public required HandleRef Anchor { get; init; }

        /// <summary>该把手当日累计时长（并集，避免重叠双计）。gate 与排序用。</summary>
        public required double TotalSeconds { get; init; }

        public required DateTimeOffset Start { get; init; }
        public required DateTimeOffset End { get; init; }

        /// <summary>与 Anchor 时间贴邻共现的少量其它高特异性把手（提示上下文，≤3；不是待勾成员集）。</summary>
        public required IReadOnlyList<HandleRef> CoOccurring { get; init; }
    }

    /// <summary>
    /// 提问器投影（ADR-028 §4，单锚点重构）：Recap 投影的第三出口。纯函数、无 I/O。
    ///
    /// 真实的一天没有空闲缝可断会话（explorer/微信/浏览器连续交错），constellation 聚簇会把
    /// 整天塌成一个 40 把手巨簇、毫无信息量。改为：每个把手独立评分，剔除已裁决 / OS 外壳 /
    /// 低特异性 / 不够时长的，按"特异性 × 未解释时长"排序，一个把手一个问题，封顶 3。
    /// </summary>
    public static class QuestionProjection
    {
        /// <summary>单把手当日累计低于此值视为噪声（复用 ADR-023 噪声地板）。</summary>
        private const int NoiseFloorSeconds = 60;

        /// <summary>非复现把手的单日时长 gate：达到才值得问（复现把手放宽，见下）。</summary>
        private const int MeaningfulSeconds = 1800;

        /// <summary>复现把手（天天出现）的更高时长 gate：ubiquity 惩罚——微信 QQ 这类要花更久才配问。</summary>
        private const int RecurringMeaningfulSeconds = 7200;

        /// <summary>每日封顶提问数（ADR-028 §4）。</summary>
        private const int MaxQuestions = 3;

        /// <summary>共现提示上下文的把手数上限（只作 LLM 提示，非待勾成员集）。</summary>
        private const int MaxCoOccurring = 3;

        /// <summary>时间贴邻：区间间隔在此内视为同一注意力语境，用于挑共现提示把手。</summary>
        private const int AdjacencySeconds = 300;

        /// <summary>
        /// OS 外壳 / 系统 chrome：忠实记录里合法，但对"你在做什么"零信息，绝不当锚点也不作提示。
        /// system Source 下匹配（browser 的 newtab 等另在 <see cref="IsShellDomain"/> 处理）。
        /// </summary>
        private static readonly HashSet<string> ShellApps = new(StringComparer.OrdinalIgnoreCase)
        {
            "explorer", "explorer.exe",
            "ShellExperienceHost", "ApplicationFrameHost", "StartMenuExperienceHost",
            "SearchHost", "SearchApp", "TextInputHost", "LockApp", "SystemSettings",
            "dwm", "sihost", "ctfmon", "RuntimeBroker", "backgroundTaskHost",
            "WindowsTerminal", "cmd", "conhost", "powershell", "pwsh",
            SyntheticApps.Away,
        };

        public static IReadOnlyList<QuestionCandidate> Project(
            IReadOnlyList<HandleInterval> intervals,
            IReadOnlySet<HandleRef> adjudicated,
            IReadOnlySet<HandleRef> recurring)
        {
            // 1. diff：已绑定/已 Mute 的把手整体退出（也含自锚优先——它们已是别的 Strand 的成员）。
            var live = intervals.Where(i => !adjudicated.Contains(i.Handle)).ToList();
            if (live.Count == 0) return [];

            // 2. per-把手聚合：并集时长 + 区间（挑共现提示用）。
            var byHandle = live
                .GroupBy(i => i.Handle)
                .Select(g => new
                {
                    Handle = g.Key,
                    Seconds = UnionSeconds([.. g]),
                    Intervals = g.OrderBy(i => i.Start).ToList(),
                    Start = g.Min(i => i.Start),
                    End = g.Max(i => i.End),
                })
                .ToList();

            // 3. 剔除：OS 外壳 / 低特异性 / 噪声地板 / 未过 gate。
            var candidates = byHandle
                .Where(h => !IsShell(h.Handle))
                .Where(h => h.Seconds >= NoiseFloorSeconds)
                .Where(h => h.Seconds >= GateFor(h.Handle, recurring))
                .ToList();
            if (candidates.Count == 0) return [];

            // 4. 排序：特异性 × 未解释时长。复现把手降权（ubiquity 惩罚，已在 gate 体现，这里再排后）。
            var lookup = candidates.ToDictionary(h => h.Handle);
            return candidates
                .OrderByDescending(h => Specificity(h.Handle, recurring))
                .ThenByDescending(h => h.Seconds)
                .ThenBy(h => h.Handle.Token, StringComparer.Ordinal)
                .Take(MaxQuestions)
                .Select(h => new QuestionCandidate
                {
                    Anchor = h.Handle,
                    TotalSeconds = h.Seconds,
                    Start = h.Start,
                    End = h.End,
                    CoOccurring = PickCoOccurring(h.Handle, h.Intervals, lookup.Keys, live),
                })
                .ToList();
        }

        /// <summary>特异性先验（ADR-028 §3 冷启动）：browser 域名 / vscode 仓库 ≫ 裸系统进程；复现再减。</summary>
        private static int Specificity(HandleRef h, IReadOnlySet<HandleRef> recurring)
        {
            var baseScore = h.Source switch
            {
                ActivitySources.Browser => 3,   // 具体域名最具身份性
                "vscode" => 3,                  // 仓库根（采集器落地后）
                ActivitySources.System => 1,    // 裸 app 名弱
                _ => 0,
            };
            // 天天出现的把手更像卫星（无处不在的基础设施），特异性再扣。
            return recurring.Contains(h) ? baseScore - 2 : baseScore;
        }

        /// <summary>时长 gate：复现把手要花更久才配问（惩罚微信 QQ 这类无处不在的把手）。</summary>
        private static int GateFor(HandleRef h, IReadOnlySet<HandleRef> recurring)
            => recurring.Contains(h) ? RecurringMeaningfulSeconds : MeaningfulSeconds;

        private static bool IsShell(HandleRef h) =>
            (h.Source == ActivitySources.System && ShellApps.Contains(h.Token))
            || (h.Source == ActivitySources.Browser && IsShellDomain(h.Token));

        /// <summary>浏览器里的非站点身份：新标签页 / 本地回环 / 空白。</summary>
        private static bool IsShellDomain(string token) =>
            token is "newtab" or "localhost" or "127.0.0.1" or "" || token.StartsWith("newtab", StringComparison.OrdinalIgnoreCase);

        /// <summary>与锚点时间贴邻、且本身也是候选（高特异性）的少量其它把手，作 LLM 提示上下文。</summary>
        private static IReadOnlyList<HandleRef> PickCoOccurring(
            HandleRef anchor, List<HandleInterval> anchorIntervals,
            IEnumerable<HandleRef> candidateHandles, List<HandleInterval> allLive)
        {
            var candidateSet = candidateHandles.ToHashSet();
            var neighbours = new HashSet<HandleRef>();
            foreach (var a in anchorIntervals)
            {
                foreach (var other in allLive)
                {
                    if (other.Handle == anchor || !candidateSet.Contains(other.Handle)) continue;
                    // 时间贴邻：区间相交或间隔 < AdjacencySeconds。
                    var gap = other.Start > a.End ? (other.Start - a.End).TotalSeconds
                            : a.Start > other.End ? (a.Start - other.End).TotalSeconds
                            : 0;
                    if (gap <= AdjacencySeconds) neighbours.Add(other.Handle);
                }
            }
            return neighbours
                .OrderBy(h => h.Token, StringComparer.Ordinal)
                .Take(MaxCoOccurring)
                .ToList();
        }

        /// <summary>区间并集时长：同把手重叠区间不双计。</summary>
        private static double UnionSeconds(List<HandleInterval> intervals)
        {
            var ordered = intervals.OrderBy(i => i.Start).ToList();
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
    }
}
