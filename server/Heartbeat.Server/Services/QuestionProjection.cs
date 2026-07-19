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
    /// 粗筛后的一个短名单候选（ADR-028 §4 定稿）：确定性层保证它"值得送分诊"，
    /// 但**不判它该不该问**——该不该问是世界知识判断，交给 LLM 分诊（QuestionService）。
    /// </summary>
    public sealed class HandleCandidate
    {
        /// <summary>候选把手。</summary>
        public required HandleRef Handle { get; init; }

        /// <summary>当日累计时长（并集，避免重叠双计）。</summary>
        public required double TotalSeconds { get; init; }

        public required DateTimeOffset Start { get; init; }
        public required DateTimeOffset End { get; init; }

        /// <summary>是否在回看窗内反复出现（分诊的先验之一：无处不在的基础设施）。</summary>
        public required bool Recurring { get; init; }

        /// <summary>与该把手时间贴邻共现的其它候选把手（分诊/提案的消歧上下文，≤3）。</summary>
        public required IReadOnlyList<HandleRef> CoOccurring { get; init; }
    }

    /// <summary>
    /// 提问器投影（ADR-028 §4 定稿）：Recap 投影的第三出口。纯函数、无 I/O。
    ///
    /// **只做确定性粗筛限流，不做选题**。哨兵剔除（OS 外壳 + 浏览器进程）、噪声地板、
    /// ubiquity 门槛，把候选缩到一个短名单送 LLM 分诊。选"该不该问"是世界知识判断，
    /// 确定性层结构上做不到，交给 QuestionService 里的分诊器（用 bind/mute 当锚）。
    ///
    /// 历史教训：曾用 constellation 聚簇（真实数据里塌成 40 把手巨簇），又用
    /// 特异性×时长打分选题（把 bilibili/github 顶到最前——它们恰恰最不该问）。两者皆被证伪。
    /// </summary>
    public static class QuestionProjection
    {
        /// <summary>单把手当日累计低于此值视为噪声（复用 ADR-023 噪声地板）。</summary>
        private const int NoiseFloorSeconds = 60;

        /// <summary>非复现把手进短名单的时长下限。</summary>
        private const int MeaningfulSeconds = 1800;

        /// <summary>复现把手（天天出现）的更高门槛：ubiquity 压制，无处不在的基础设施要花更久才进名单。</summary>
        private const int RecurringMeaningfulSeconds = 7200;

        /// <summary>短名单上限：控 LLM 分诊调用量（每个候选一次调用）。</summary>
        private const int MaxShortlist = 8;

        /// <summary>共现提示上下文的把手数上限。</summary>
        private const int MaxCoOccurring = 3;

        /// <summary>时间贴邻：区间间隔在此内视为同一注意力语境，用于挑共现提示把手。</summary>
        private const int AdjacencySeconds = 300;

        /// <summary>
        /// OS 外壳 + 浏览器进程 + away：对"你在做什么"零信息，绝不当候选也不作提示。
        /// 浏览器进程（msedge/chrome…）是卫星工具，且与 browser 源的域名把手重复计数——
        /// 真正的身份在域名那侧，进程名这侧永远压掉（ADR-028 §4 定稿）。
        /// </summary>
        private static readonly HashSet<string> ShellApps = new(StringComparer.OrdinalIgnoreCase)
        {
            "explorer", "explorer.exe",
            "ShellExperienceHost", "ApplicationFrameHost", "StartMenuExperienceHost",
            "SearchHost", "SearchApp", "TextInputHost", "LockApp", "SystemSettings",
            "dwm", "sihost", "ctfmon", "RuntimeBroker", "backgroundTaskHost",
            "WindowsTerminal", "cmd", "conhost", "powershell", "pwsh",
            // 浏览器进程：卫星，身份在 browser 域名把手那侧。
            "msedge", "chrome", "firefox", "iexplore", "opera", "brave", "vivaldi",
            "msedgewebview2", "WeChatAppEx", "WeChatBrowser",
            SyntheticApps.Away,
        };

        /// <summary>确定性粗筛：产出送 LLM 分诊的短名单。不选题、不打分排序。</summary>
        public static IReadOnlyList<HandleCandidate> Shortlist(
            IReadOnlyList<HandleInterval> intervals,
            IReadOnlySet<HandleRef> adjudicated,
            IReadOnlySet<HandleRef> recurring)
        {
            // 1. diff：已绑定/已 Mute 的把手整体退出。
            var live = intervals.Where(i => !adjudicated.Contains(i.Handle)).ToList();
            if (live.Count == 0) return [];

            // 2. per-把手聚合：并集时长 + 区间。
            var byHandle = live
                .GroupBy(i => i.Handle)
                .Select(g => new
                {
                    Handle = g.Key,
                    Seconds = UnionSeconds([.. g]),
                    Start = g.Min(i => i.Start),
                    End = g.Max(i => i.End),
                })
                .ToList();

            // 3. 粗筛：哨兵剔除 / 噪声地板 / ubiquity 门槛。
            var candidates = byHandle
                .Where(h => !IsShell(h.Handle))
                .Where(h => h.Seconds >= NoiseFloorSeconds)
                .Where(h => h.Seconds >= GateFor(h.Handle, recurring))
                .ToList();
            if (candidates.Count == 0) return [];

            var candidateSet = candidates.Select(h => h.Handle).ToHashSet();

            // 4. 限流：仅按时长取头部若干送分诊（限流，非选题——谁该问由分诊定）。
            return candidates
                .OrderByDescending(h => h.Seconds)
                .Take(MaxShortlist)
                .Select(h => new HandleCandidate
                {
                    Handle = h.Handle,
                    TotalSeconds = h.Seconds,
                    Start = h.Start,
                    End = h.End,
                    Recurring = recurring.Contains(h.Handle),
                    CoOccurring = PickCoOccurring(h.Handle, live, candidateSet),
                })
                .ToList();
        }

        /// <summary>时长门槛：复现把手要花更久才进名单（ubiquity 压制）。</summary>
        private static int GateFor(HandleRef h, IReadOnlySet<HandleRef> recurring)
            => recurring.Contains(h) ? RecurringMeaningfulSeconds : MeaningfulSeconds;

        private static bool IsShell(HandleRef h) =>
            (h.Source == ActivitySources.System && ShellApps.Contains(h.Token))
            || (h.Source == ActivitySources.Browser && IsShellDomain(h.Token));

        /// <summary>浏览器里的非站点身份：新标签页 / 本地回环 / 空白。</summary>
        private static bool IsShellDomain(string token) =>
            token is "newtab" or "localhost" or "127.0.0.1" or ""
            || token.StartsWith("newtab", StringComparison.OrdinalIgnoreCase);

        /// <summary>与把手时间贴邻、且本身也在短名单的少量其它把手，作分诊/提案消歧上下文。</summary>
        private static IReadOnlyList<HandleRef> PickCoOccurring(
            HandleRef anchor, List<HandleInterval> allLive, IReadOnlySet<HandleRef> candidateSet)
        {
            var anchorIntervals = allLive.Where(i => i.Handle == anchor).ToList();
            var neighbours = new HashSet<HandleRef>();
            foreach (var a in anchorIntervals)
            {
                foreach (var other in allLive)
                {
                    if (other.Handle == anchor || !candidateSet.Contains(other.Handle)) continue;
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
