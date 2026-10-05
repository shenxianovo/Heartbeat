using System.Text.Json;

namespace Heartbeat.Dev;

/// <summary>
/// 生产函数复杂度大于 10 的数量上限，每次 quality 都检查，与 Git 基点无关。
/// 预算只应下调；上调须修改预算文件并说明理由。
/// </summary>
internal sealed record StockBudgetLimits(
    int MaxProductionComplexityHotspots,
    string? RecordedAt = null,
    string? MeasuredAgainstBase = null,
    string? Rule = null);

internal sealed record StockBudgetReport(
    bool Available,
    bool Passed,
    string Path,
    int? Limit,
    int Current,
    string? Reason);

internal static class StockBudget
{
    public const string FileName = "quality-budget.json";

    public static StockBudgetReport Evaluate(RepositoryContext repository, int currentHotspots)
    {
        var path = repository.Path("tools", "Heartbeat.Dev", FileName);
        return Evaluate(path, File.Exists(path) ? File.ReadAllText(path) : null, currentHotspots);
    }

    internal static StockBudgetReport Evaluate(string path, string? json, int currentHotspots)
    {
        var relative = Relative(path);
        if (json is null)
        {
            return new StockBudgetReport(false, false, relative, null, currentHotspots,
                $"The stock complexity budget is missing. Create {relative} with "
                + "\"maxProductionComplexityHotspots\" set to the currently measured hotspot count.");
        }

        StockBudgetLimits? limits;
        try
        {
            limits = JsonSerializer.Deserialize<StockBudgetLimits>(json, JsonOptions.Indented);
        }
        catch (JsonException exception)
        {
            return new StockBudgetReport(false, false, relative, null, currentHotspots,
                $"Could not read the stock complexity budget {relative}: {exception.Message}");
        }

        if (limits is null || limits.MaxProductionComplexityHotspots < 0)
        {
            return new StockBudgetReport(false, false, relative, null, currentHotspots,
                $"{relative} must set \"maxProductionComplexityHotspots\" to a non-negative integer.");
        }

        var limit = limits.MaxProductionComplexityHotspots;
        return currentHotspots <= limit
            ? new StockBudgetReport(true, true, relative, limit, currentHotspots, null)
            : new StockBudgetReport(true, false, relative, limit, currentHotspots,
                $"{currentHotspots} production functions are above complexity 10; the committed stock budget is {limit}. "
                + $"Bring the count back down, or raise the number in {relative} and say why there — this budget is meant to move down only.");
    }

    private static string Relative(string path) =>
        path.Replace('\\', '/') is var normalized && normalized.Contains("/tools/", StringComparison.Ordinal)
            ? normalized[(normalized.IndexOf("/tools/", StringComparison.Ordinal) + 1)..]
            : normalized;
}
