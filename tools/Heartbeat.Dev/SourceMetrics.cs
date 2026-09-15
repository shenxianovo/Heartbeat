using System.Collections.ObjectModel;

namespace Heartbeat.Dev;

internal enum SourceRole
{
    Production,
    Test,
    Tooling,
    Documentation,
    Generated,
}

internal sealed record SourceFileMetric(string Path, string Language, SourceRole Role, int Lines);

internal sealed record SourceSnapshot(string Revision, IReadOnlyList<SourceFileMetric> Files)
{
    public IReadOnlyDictionary<string, int> ProductionByLanguage => new ReadOnlyDictionary<string, int>(
        Files.Where(file => file.Role == SourceRole.Production)
            .GroupBy(file => file.Language, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Sum(file => file.Lines), StringComparer.Ordinal));

    public int ProductionLines => Files.Where(file => file.Role == SourceRole.Production).Sum(file => file.Lines);
    public int TestLines => Files.Where(file => file.Role == SourceRole.Test).Sum(file => file.Lines);
    public int ToolingLines => Files.Where(file => file.Role == SourceRole.Tooling).Sum(file => file.Lines);
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

        if (IsGenerated(normalized, name))
        {
            role = SourceRole.Generated;
        }
        else if (IsTest(normalized, name))
        {
            role = SourceRole.Test;
        }
        else if (language == "Markdown")
        {
            role = SourceRole.Documentation;
        }
        else if (normalized.StartsWith("src/", StringComparison.Ordinal))
        {
            role = SourceRole.Production;
        }
        else
        {
            role = SourceRole.Tooling;
        }
        return true;
    }

    private static bool IsGenerated(string path, string name) =>
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
