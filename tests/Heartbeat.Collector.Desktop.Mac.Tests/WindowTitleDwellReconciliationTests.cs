using System.Text.Json;
using Heartbeat.Collector.Desktop.Mac;
using Heartbeat.Hub;

namespace Heartbeat.Collector.Desktop.Mac.Tests;

/// <summary>
/// 对账测试的生产一侧。窗口标题静置规则的权威实现是 <see cref="DesktopRecordProjector"/>
/// （<c>ObserveWindowTitle</c> / <c>SettlePendingTitle</c>）；Developer CLI 的探针
/// （<c>tools/Heartbeat.Dev/WindowTitleChurn.cs</c>）里有一份模拟，用来在真实读数上比不同静置参数。
/// 两侧读同一张场景表 <c>tests/window-title-dwell-scenarios.json</c>，各自断言自己那一列：
/// 这里钉住生产规则真的会写出几条窗口 Record。规则改了而表没改，这个测试就红。
/// </summary>
public sealed class WindowTitleDwellReconciliationTests
{
    private const string WindowTrack = "desktop.window.foreground";
    private static readonly DateTimeOffset Start = new(2026, 9, 16, 9, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions TableFormat = new() { PropertyNameCaseInsensitive = true };

    public static TheoryData<string> ScenarioNames()
    {
        var names = new TheoryData<string>();
        foreach (var scenario in Load())
        {
            names.Add(scenario.Name);
        }
        return names;
    }

    [Theory]
    [MemberData(nameof(ScenarioNames))]
    public void ProductionRuleMatchesTheSharedScenarioTable(string name)
    {
        var scenario = Load().Single(item => string.Equals(item.Name, name, StringComparison.Ordinal));
        var staged = new List<(SubmissionRoute Route, RecordSnapshot Record)>();
        var projector = new DesktopRecordProjector(
            "device-a",
            "Mac",
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(scenario.DwellSeconds),
            (route, record) => staged.Add((route, record)));

        foreach (var reading in scenario.Readings)
        {
            projector.Apply(
                new MacSystemObservation.Activity(new DesktopActivitySample(
                    new ForegroundApplication("macos", "bundle_id", reading.Application, reading.Application),
                    reading.Title)),
                Start.AddSeconds(reading.Second));
        }

        var windowRecords = staged
            .Where(item => item.Route.Track.Type == WindowTrack)
            .Select(item => item.Record.Id)
            .Distinct()
            .Count();

        Assert.Equal(scenario.ProductionWindowRecords, windowRecords);
    }

    private static TableScenario[] Load() =>
        JsonSerializer.Deserialize<Table>(File.ReadAllText(TablePath), TableFormat)!.Scenarios;

    private static string TablePath
    {
        get
        {
            var current = AppContext.BaseDirectory;
            while (!File.Exists(Path.Combine(current, "Heartbeat.slnx")))
            {
                current = Directory.GetParent(current)?.FullName ?? throw new InvalidOperationException(
                    $"Could not find the repository root above {AppContext.BaseDirectory}.");
            }
            return Path.Combine(current, "tests", "window-title-dwell-scenarios.json");
        }
    }

    private sealed record Table(TableScenario[] Scenarios);

    private sealed record TableScenario(
        string Name,
        double DwellSeconds,
        TableReading[] Readings,
        int ProbeWindowRecords,
        int ProductionWindowRecords,
        string? Divergence);

    private sealed record TableReading(double Second, string Application, string? Title);
}
