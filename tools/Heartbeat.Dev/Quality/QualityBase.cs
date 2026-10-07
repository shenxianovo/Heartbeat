namespace Heartbeat.Dev;

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

        suggestions.Add("Use the commit you started from (--base HEAD for uncommitted work).");
        return new BaseUsability(false, reason, suggestions);
    }

    private static string Short(string revision) => revision.Length > 7 ? revision[..7] : revision;
}
