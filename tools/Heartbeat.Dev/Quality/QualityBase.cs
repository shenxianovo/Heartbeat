namespace Heartbeat.Dev;

/// <summary>
/// 存量度量固定使用重写后首个包含完整项目结构的提交。
/// 重写前的 `main` 没有 `src/` 生产目录，无法作为基点。
/// </summary>
internal static class RewriteLineage
{
    public const string Alias = "anchor";
    public const string AnchorCommit = "4e15d57beea301c963951fdcef4c9b5a9264212f";
    public const string AnchorSubject = "feat(recording): implement timeline storage model";

    public static string Describe() => $"{AnchorCommit[..7]} ({AnchorSubject})";

    /// `--base anchor` 解析成锚点提交，其它值原样交给 Git。
    public static string Resolve(string baseRef) =>
        string.Equals(baseRef, Alias, StringComparison.Ordinal) ? AnchorCommit : baseRef;

    public static string StockCommand() => $"dotnet run --project tools/Heartbeat.Dev -- quality --base {Alias} --stock";
}

/// 扫描前检查基点是否包含生产代码，并为无效基点提供建议。
internal sealed record BaseUsability(bool Usable, string? Reason, IReadOnlyList<string> Suggestions)
{
    public static BaseUsability Evaluate(string requested, SourceSnapshot baseline)
    {
        if (baseline.ProductionFiles > 0)
        {
            return new BaseUsability(true, null, []);
        }

        var roots = baseline.CodeRoots;
        var reason =
            $"Git base '{requested}' ({Short(baseline.Revision)}) has no measurable production code: "
            + $"0 production files under src/, out of {baseline.Files.Count} recognized files. "
            + "This is almost certainly the wrong base, not a project without code.";
        var suggestions = new List<string>();
        if (roots.Count > 0)
        {
            suggestions.Add(
                $"That tree keeps code under {string.Join(", ", roots.Select(root => root + "/"))}; "
                + "this repository measures production code under src/, so the base predates the current layout.");
        }
        suggestions.Add(
            $"To measure stock, use the rewrite anchor {RewriteLineage.Describe()}: {RewriteLineage.StockCommand()}");
        suggestions.Add("To gate a change, use the commit you started from (--base HEAD for uncommitted work).");
        return new BaseUsability(false, reason, suggestions);
    }

    private static string Short(string revision) => revision.Length > 7 ? revision[..7] : revision;
}
