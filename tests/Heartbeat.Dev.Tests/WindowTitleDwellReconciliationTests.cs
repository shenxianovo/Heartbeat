using System.Globalization;
using System.Text.Json;
using Heartbeat.Dev;

namespace Heartbeat.Dev.Tests;

/// <summary>
/// 对账测试的探针一侧。<c>WindowTitleChurn.Simulate</c> 是生产静置规则
/// （<c>src/Collectors/Heartbeat.Collector.Desktop/DesktopRecordProjector.cs</c>）的一份模拟，
/// 用来选参数；权威在生产那一侧。两侧读同一张场景表 <c>tests/window-title-dwell-scenarios.json</c>：
/// 这里钉住模拟给出的 Record 条数，<c>tests/Heartbeat.Collector.Desktop.Mac.Tests</c> 里的同名测试
/// 钉住生产规则给出的条数。任一侧漂移，对应那一侧就红；两个数字不一样的行必须在表里写清为什么。
/// </summary>
public sealed class WindowTitleDwellReconciliationTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 16, 9, 0, 0, TimeSpan.Zero);

    public static TheoryData<string> ScenarioNames()
    {
        var data = new TheoryData<string>();
        foreach (var scenario in DwellScenarioTable.Load())
        {
            data.Add(scenario.Name);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(ScenarioNames))]
    public void SimulationMatchesTheSharedScenarioTable(string name)
    {
        var scenario = DwellScenarioTable.Single(name);
        var readings = scenario.Readings
            .Select(reading => new WindowTitleReading(
                Start.AddSeconds(reading.Second),
                "poll",
                reading.Application,
                reading.Title,
                reading.Title?.Length ?? 0,
                RotationOfPrevious: false))
            .ToArray();

        var outcome = WindowTitleChurn.Analyze(readings, [scenario.DwellSeconds]).Dwells.Single();

        Assert.Equal(scenario.ProbeWindowRecords, outcome.WindowRecords);
        if (outcome.WindowRecords == scenario.ProductionWindowRecords)
        {
            Assert.Null(scenario.Divergence);
        }
        else
        {
            Assert.False(string.IsNullOrWhiteSpace(scenario.Divergence),
                $"Scenario '{name}' must explain why the probe and production rule differ.");
        }
    }
}

/// 场景表的读取。两个项目各读一遍，各自断言自己那一侧的数字：共享的是数据，不是代码。
internal static class DwellScenarioTable
{
    internal sealed record Reading(double Second, string Application, string? Title);

    internal sealed record Scenario(
        string Name,
        double DwellSeconds,
        IReadOnlyList<Reading> Readings,
        int ProbeWindowRecords,
        int ProductionWindowRecords,
        string? Divergence);

    public static IReadOnlyList<Scenario> Load()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path()));
        return [.. document.RootElement.GetProperty("scenarios").EnumerateArray().Select(Read)];
    }

    public static Scenario Single(string name) =>
        Load().Single(scenario => string.Equals(scenario.Name, name, StringComparison.Ordinal));

    private static Scenario Read(JsonElement element) => new(
        element.GetProperty("name").GetString()!,
        element.GetProperty("dwellSeconds").GetDouble(),
        [.. element.GetProperty("readings").EnumerateArray().Select(reading => new Reading(
            reading.GetProperty("second").GetDouble(),
            reading.GetProperty("application").GetString()!,
            reading.GetProperty("title").GetString()))],
        element.GetProperty("probeWindowRecords").GetInt32(),
        element.GetProperty("productionWindowRecords").GetInt32(),
        element.GetProperty("divergence").GetString());

    private static string Path()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(System.IO.Path.Combine(directory.FullName, "Heartbeat.slnx")))
        {
            directory = directory.Parent;
        }
        var path = System.IO.Path.Combine(
            directory?.FullName ?? throw new InvalidOperationException(
                string.Format(CultureInfo.InvariantCulture, "Could not find the repository root above {0}.", AppContext.BaseDirectory)),
            "tests",
            "window-title-dwell-scenarios.json");
        return File.Exists(path)
            ? path
            : throw new InvalidOperationException($"The shared dwell scenario table is missing: {path}");
    }
}
