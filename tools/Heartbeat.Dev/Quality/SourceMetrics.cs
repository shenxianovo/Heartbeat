using System.Collections.ObjectModel;

namespace Heartbeat.Dev;

internal enum SourceRole
{
    Production,
    Test,
    Tooling,

    /// 构建与环境描述：MSBuild、解决方案、Dockerfile、compose YAML、前端 *.config.*。
    /// 它们是代码，但不是被度量的产品语料，所以不计入生产 LOC。
    Build,
    Documentation,
    Generated,

    /// 认得出语言，但不落在任何一个已声明的根下。宁可单列出来让人看见，也不塞进 tooling。
    Unclassified,
}

internal sealed record SourceFileMetric(string Path, string Language, SourceRole Role, int Lines);

internal sealed record SourceSnapshot(string Revision, IReadOnlyList<SourceFileMetric> Files)
{
    public IReadOnlyDictionary<string, int> ProductionByLanguage => new ReadOnlyDictionary<string, int>(
        Files.Where(file => file.Role == SourceRole.Production)
            .GroupBy(file => file.Language, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Sum(file => file.Lines), StringComparer.Ordinal));

    public int ProductionLines => Lines(SourceRole.Production);
    public int ProductionFiles => Files.Count(file => file.Role == SourceRole.Production);
    public int TestLines => Lines(SourceRole.Test);
    public int ToolingLines => Lines(SourceRole.Tooling);
    public int BuildLines => Lines(SourceRole.Build);
    public int UnclassifiedLines => Lines(SourceRole.Unclassified);

    /// 未归类文件的路径，按目录归并，让「兜底去哪了」在报告里看得见。
    public IReadOnlyList<string> UnclassifiedPaths => [.. Files
        .Where(file => file.Role == SourceRole.Unclassified)
        .Select(file => file.Path)
        .Order(StringComparer.Ordinal)];

    /// 基点树里代码实际住在哪些顶层目录。选错基点时用它说明「你的源码在这儿，但这里不是生产根」。
    /// 只报目录：仓库根下的单个文件不是「代码住的地方」，把它们也写成 `x/` 只会让诊断显得像在猜。
    public IReadOnlyList<string> CodeRoots => [.. Files
        .Where(file => file.Role is not (SourceRole.Documentation or SourceRole.Generated))
        .Select(file => file.Path.Split('/'))
        .Where(segments => segments.Length > 1)
        .Select(segments => segments[0])
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)];

    private int Lines(SourceRole role) => Files.Where(file => file.Role == role).Sum(file => file.Lines);
}

internal static class SourceCorpus
{
    private static readonly IReadOnlyDictionary<string, string> Languages =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".cs"] = "C#",
            [".css"] = "CSS",
            [".js"] = "JavaScript",
            [".jsx"] = "JavaScript",
            [".mjs"] = "JavaScript",
            [".cjs"] = "JavaScript",
            [".ts"] = "TypeScript",
            [".tsx"] = "TypeScript",
            [".mts"] = "TypeScript",
            [".cts"] = "TypeScript",
            [".sh"] = "Shell",
            [".ps1"] = "PowerShell",
            [".py"] = "Python",
            [".yaml"] = "YAML",
            [".yml"] = "YAML",
            [".csproj"] = "MSBuild",
            [".props"] = "MSBuild",
            [".targets"] = "MSBuild",
            [".slnx"] = "XML",
            [".md"] = "Markdown",
        };

    /// 归类规则是显式的、有顺序的：生成物 → 测试 → 文档 → 构建描述 → 生产根 → 工具根 → 未归类。
    /// 没有兜底：不在任何一个已声明的根下的文件归 Unclassified，由报告单列，而不是悄悄记进 tooling。
    private static readonly string[] ProductionRoots = ["src/"];

    private static readonly string[] ToolingRoots =
        // Historical baselines still contain script-based development tooling.
        ["tools/", "scripts/", ".agents/", ".config/", ".github/"];

    private static readonly string[] BuildLanguages = ["MSBuild", "XML", "Dockerfile", "YAML"];

    public static bool TryClassify(string path, out string language, out SourceRole role)
    {
        var normalized = path.Replace('\\', '/');
        var name = Path.GetFileName(normalized);
        language = name == "Dockerfile"
            ? "Dockerfile"
            : Languages.GetValueOrDefault(Path.GetExtension(name)) ?? string.Empty;
        if (language.Length == 0)
        {
            role = default;
            return false;
        }

        role = Classify(normalized, name, language);
        return true;
    }

    private static SourceRole Classify(string path, string name, string language) =>
        IsGenerated(name) ? SourceRole.Generated
        : IsTest(path, name) ? SourceRole.Test
        : language == "Markdown" ? SourceRole.Documentation
        : IsBuild(name, language) ? SourceRole.Build
        : StartsWithAny(path, ProductionRoots) ? SourceRole.Production
        : StartsWithAny(path, ToolingRoots) ? SourceRole.Tooling
        : SourceRole.Unclassified;

    private static bool StartsWithAny(string path, IReadOnlyList<string> roots) =>
        roots.Any(root => path.StartsWith(root, StringComparison.Ordinal));

    /// 构建与环境描述不是产品语料：项目文件、解决方案、镜像与 compose、前端工具链配置。
    private static bool IsBuild(string name, string language) =>
        BuildLanguages.Contains(language, StringComparer.Ordinal)
        || name.Contains(".config.", StringComparison.Ordinal);

    private static bool IsGenerated(string name) =>
        name.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith("ModelSnapshot.cs", StringComparison.OrdinalIgnoreCase)
        || name is "next-env.d.ts"
        || name.EndsWith("-lock.json", StringComparison.OrdinalIgnoreCase)
        || name is "package-lock.json";

    private static bool IsTest(string path, string name)
    {
        var segments = path.Split('/');
        if (segments[..^1].Any(segment => segment.Equals("tests", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("test", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("__tests__", StringComparison.OrdinalIgnoreCase)
            || segment.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }
        var parts = name.Split('.');
        return parts.Length > 2 && parts[1..^1].Any(part => part is "test" or "spec");
    }
}

internal static class SourceLineCounter
{
    private static readonly IReadOnlyDictionary<string, string[]> LineComments =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["C#"] = ["//"],
            ["Dockerfile"] = ["#"],
            ["JavaScript"] = ["//"],
            ["PowerShell"] = ["#"],
            ["Python"] = ["#"],
            ["Shell"] = ["#"],
            ["TypeScript"] = ["//"],
            ["YAML"] = ["#"],
        };

    private static readonly IReadOnlyDictionary<string, (string Start, string End)[]> BlockComments =
        new Dictionary<string, (string, string)[]>(StringComparer.Ordinal)
        {
            ["C#"] = [("/*", "*/")],
            ["CSS"] = [("/*", "*/")],
            ["JavaScript"] = [("/*", "*/")],
            ["TypeScript"] = [("/*", "*/")],
            ["Markdown"] = [("<!--", "-->")],
            ["MSBuild"] = [("<!--", "-->")],
            ["PowerShell"] = [("<#", "#>")],
            ["XML"] = [("<!--", "-->")],
        };

    public static int Count(string text, string language)
    {
        var lineTokens = LineComments.GetValueOrDefault(language) ?? [];
        var blockPairs = BlockComments.GetValueOrDefault(language) ?? [];
        string? activeBlockEnd = null;
        var effectiveLines = 0;
        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (LineHasCode(line, lineTokens, blockPairs, ref activeBlockEnd)) effectiveLines++;
        }
        return effectiveLines;
    }

    private static bool LineHasCode(
        string line,
        IReadOnlyList<string> lineTokens,
        IReadOnlyList<(string Start, string End)> blockPairs,
        ref string? activeBlockEnd)
    {
        var position = 0;
        var hasCode = false;
        char? quote = null;
        var escaped = false;
        while (position < line.Length)
        {
            if (ConsumeActiveBlock(line, ref position, ref activeBlockEnd)) continue;
            if (activeBlockEnd is not null) break;
            if (ConsumeQuotedCharacter(line, ref position, ref quote, ref escaped, ref hasCode)) continue;
            if (StartsLineComment(line, position, lineTokens)) break;
            if (StartBlock(line, ref position, blockPairs, ref activeBlockEnd)) continue;
            if (!char.IsWhiteSpace(line[position])) hasCode = true;
            position++;
        }
        return hasCode;
    }

    private static bool ConsumeActiveBlock(string line, ref int position, ref string? activeBlockEnd)
    {
        if (activeBlockEnd is null) return false;
        var end = line.IndexOf(activeBlockEnd, position, StringComparison.Ordinal);
        if (end < 0) return false;
        position = end + activeBlockEnd.Length;
        activeBlockEnd = null;
        return true;
    }

    private static bool ConsumeQuotedCharacter(
        string line,
        ref int position,
        ref char? quote,
        ref bool escaped,
        ref bool hasCode)
    {
        var character = line[position];
        if (quote is null)
        {
            if (character is not ('"' or '\'' or '`')) return false;
            quote = character;
            hasCode = true;
            position++;
            return true;
        }
        if (!char.IsWhiteSpace(character)) hasCode = true;
        if (escaped) escaped = false;
        else if (character == '\\') escaped = true;
        else if (character == quote) quote = null;
        position++;
        return true;
    }

    private static bool StartsLineComment(string line, int position, IReadOnlyList<string> tokens) =>
        tokens.Any(token => line.AsSpan(position).StartsWith(token, StringComparison.Ordinal));

    private static bool StartBlock(
        string line,
        ref int position,
        IReadOnlyList<(string Start, string End)> pairs,
        ref string? activeBlockEnd)
    {
        foreach (var pair in pairs)
        {
            if (!line.AsSpan(position).StartsWith(pair.Start, StringComparison.Ordinal)) continue;
            activeBlockEnd = pair.End;
            position += pair.Start.Length;
            return true;
        }
        return false;
    }
}
