using System.Globalization;

namespace Heartbeat.Dev;

/// 探针读数的一次采样。TitleHash 为 null 表示当时读不到标题；
/// Origin 区分「原生通知送来的」与「轮询读到的」，用于判断能否信任通知。
internal sealed record WindowTitleReading(
    DateTimeOffset At,
    string Origin,
    string? Application,
    string? TitleHash,
    int TitleLength,
    bool RotationOfPrevious);

/// 一次候选去抖参数下的模拟结果：静置 DwellSeconds 后才承认新标题，会留下多少条窗口 Record。
internal sealed record DwellOutcome(
    double DwellSeconds,
    int WindowRecords,
    int SuppressedChanges,
    double UnstableSeconds,
    double LongestUnstableStretchSeconds);

internal sealed record ApplicationChurn(
    string Application,
    int TitleChanges,
    int RotationLikeChanges,
    double? MedianHoldSeconds,
    double? ShortestHoldSeconds);

internal sealed record WindowTitleChurnReport(
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    double DurationSeconds,
    int Readings,
    int EventReadings,
    int PollReadings,
    int ObservedValues,
    int ApplicationSwitches,
    int TitleChanges,
    int TitleChangesFirstSeenByPoll,
    int RotationLikeChanges,
    int SameLengthChanges,
    int TitleChangesWithinOneSecond,
    int TitleUnavailableReadings,
    double? MedianHoldSeconds,
    double? TenthPercentileHoldSeconds,
    double? ShortestHoldSeconds,
    IReadOnlyList<ApplicationChurn> Applications,
    IReadOnlyList<DwellOutcome> Dwells);

/// <summary>
/// 把探针读数变成「按现在的规则会切出多少条 Record」和「静置多久能压掉多少」的可比数字。
/// 这里只做算术，不决定规则：参数由数据决定，而不是先猜一个阈值再找证据。
/// </summary>
/// <remarks>
/// 规则权威是 <c>src/Collectors/Heartbeat.Collector.Desktop/DesktopRecordProjector.cs</c>
/// （<c>ObserveWindowTitle</c> / <c>SettlePendingTitle</c>）。这里的 <c>Simulate</c> 是它的一份
/// 模拟，供选参数用；两边都改才算改完。项目引用方向是 tools → src 不通，所以用一张共享场景表
/// 对账：<c>tests/window-title-dwell-scenarios.json</c> 每行给出同一串读数在同一静置参数下
/// 「模拟会留几条 Record」与「生产规则会留几条 Record」，
/// <c>tests/Heartbeat.Dev.Tests/WindowTitleDwellReconciliationTests.cs</c> 钉住前者，
/// <c>tests/Heartbeat.Collector.Desktop.Mac.Tests/WindowTitleDwellReconciliationTests.cs</c>
/// 用真实 projector 钉住后者，任一侧漂移就红。两个数字不一致的行（读不到标题、静置为 0 时的末尾读数）
/// 必须在表里写清原因，测试会检查这一点——已知差异靠表维持，不靠记忆。
/// </remarks>
internal static class WindowTitleChurn
{
    public static readonly double[] DefaultDwellSeconds = [0.25, 0.5, 1, 2, 5];

    public static WindowTitleChurnReport Analyze(
        IReadOnlyList<WindowTitleReading> readings,
        IReadOnlyList<double>? dwellSeconds = null)
    {
        var ordered = readings.OrderBy(reading => reading.At).ToArray();
        if (ordered.Length == 0)
        {
            throw new InvalidOperationException("The probe produced no readings; nothing can be concluded from it.");
        }

        var timeline = Collapse(ordered);
        var changes = Changes(timeline);
        var titleChanges = changes.Where(change => !change.ApplicationSwitch).ToArray();
        var holds = titleChanges.Select(change => change.HeldSeconds).Order().ToArray();
        var completedAt = ordered[^1].At;
        return new WindowTitleChurnReport(
            ordered[0].At,
            completedAt,
            (completedAt - ordered[0].At).TotalSeconds,
            ordered.Length,
            ordered.Count(reading => reading.Origin == "event"),
            ordered.Count(reading => reading.Origin == "poll"),
            timeline.Count,
            changes.Count(change => change.ApplicationSwitch),
            titleChanges.Length,
            titleChanges.Count(change => change.Origin == "poll"),
            titleChanges.Count(change => change.RotationLike),
            titleChanges.Count(change => change.SameLength),
            titleChanges.Count(change => change.HeldSeconds < 1),
            ordered.Count(reading => reading.TitleHash is null),
            Percentile(holds, 0.5),
            Percentile(holds, 0.1),
            holds.Length == 0 ? null : holds[0],
            PerApplication(titleChanges),
            [.. (dwellSeconds ?? DefaultDwellSeconds).Select(dwell => Simulate(timeline, completedAt, dwell))]);
    }

    /// 相邻读数值相同的合并掉：轮询会把同一个读数重复上报，重复不是变化。
    private static List<WindowTitleReading> Collapse(WindowTitleReading[] ordered)
    {
        var timeline = new List<WindowTitleReading> { ordered[0] };
        foreach (var reading in ordered.Skip(1))
        {
            var previous = timeline[^1];
            if (previous.Application != reading.Application || previous.TitleHash != reading.TitleHash)
            {
                timeline.Add(reading);
            }
        }
        return timeline;
    }

    private static Change[] Changes(List<WindowTitleReading> timeline) =>
        [.. timeline.Skip(1).Select((reading, index) =>
        {
            var previous = timeline[index];
            return new Change(
                reading.At,
                reading.Origin,
                reading.Application,
                previous.Application != reading.Application,
                reading.RotationOfPrevious,
                previous.TitleLength == reading.TitleLength,
                (reading.At - previous.At).TotalSeconds);
        })];

    private static IReadOnlyList<ApplicationChurn> PerApplication(IReadOnlyList<Change> titleChanges) =>
        [.. titleChanges
            .GroupBy(change => change.Application ?? "(none)", StringComparer.Ordinal)
            .Select(group =>
            {
                var holds = group.Select(change => change.HeldSeconds).Order().ToArray();
                return new ApplicationChurn(
                    group.Key,
                    group.Count(),
                    group.Count(change => change.RotationLike),
                    Percentile(holds, 0.5),
                    holds[0]);
            })
            .OrderByDescending(application => application.TitleChanges)];

    /// 模拟「新读数必须静置 dwell 才承认」的规则。应用身份变化立即承认：应用切换不是噪声。
    private static DwellOutcome Simulate(
        List<WindowTitleReading> timeline,
        DateTimeOffset completedAt,
        double dwellSeconds)
    {
        var dwell = TimeSpan.FromSeconds(dwellSeconds);
        var committed = timeline[0];
        var records = 1;
        var suppressed = 0;
        var unstable = TimeSpan.Zero;
        var stretch = TimeSpan.Zero;
        var longestStretch = TimeSpan.Zero;
        for (var index = 1; index < timeline.Count; index++)
        {
            var candidate = timeline[index];
            var held = (index + 1 < timeline.Count ? timeline[index + 1].At : completedAt) - candidate.At;
            if (candidate.Application != committed.Application || held >= dwell)
            {
                records++;
                committed = candidate;
                longestStretch = Max(longestStretch, stretch);
                stretch = TimeSpan.Zero;
                continue;
            }

            if (candidate.TitleHash == committed.TitleHash)
            {
                continue;
            }

            suppressed++;
            unstable += held;
            stretch += held;
        }

        return new DwellOutcome(
            dwellSeconds, records, suppressed, unstable.TotalSeconds, Max(longestStretch, stretch).TotalSeconds);
    }

    private static TimeSpan Max(TimeSpan left, TimeSpan right) => left > right ? left : right;

    private static double? Percentile(double[] sorted, double fraction)
    {
        if (sorted.Length == 0)
        {
            return null;
        }
        var rank = (int)Math.Ceiling(fraction * sorted.Length) - 1;
        return sorted[Math.Clamp(rank, 0, sorted.Length - 1)];
    }

    public static bool TitleWasNeverReadable(WindowTitleChurnReport report) =>
        report.TitleUnavailableReadings == report.Readings;

    public static string Summarize(WindowTitleChurnReport report)
    {
        var lines = new List<string>
        {
            $"Duration {Format(report.DurationSeconds)}s, readings {report.Readings} (events {report.EventReadings},"
                + $" polls {report.PollReadings}, other {report.Readings - report.EventReadings - report.PollReadings}).",
            $"Observed values {report.ObservedValues}: {report.ApplicationSwitches} application switches, {report.TitleChanges} title changes"
                + $" ({report.TitleChangesFirstSeenByPoll} first seen by polling, {report.RotationLikeChanges} rotation-like, {report.TitleChangesWithinOneSecond} within one second).",
            $"Title hold seconds: p10 {Format(report.TenthPercentileHoldSeconds)}, p50 {Format(report.MedianHoldSeconds)}, shortest {Format(report.ShortestHoldSeconds)}.",
        };
        lines.AddRange(report.Dwells.Select(dwell =>
            $"Dwell {Format(dwell.DwellSeconds)}s would keep {dwell.WindowRecords} window records, suppress {dwell.SuppressedChanges} changes,"
                + $" and show a stale title for {Format(dwell.UnstableSeconds)}s (longest stretch {Format(dwell.LongestUnstableStretchSeconds)}s)."));
        return string.Join(Environment.NewLine, lines);
    }

    private static string Format(double? value) =>
        value is null ? "n/a" : value.Value.ToString("F2", CultureInfo.InvariantCulture);

    private sealed record Change(
        DateTimeOffset At,
        string Origin,
        string? Application,
        bool ApplicationSwitch,
        bool RotationLike,
        bool SameLength,
        double HeldSeconds);
}
