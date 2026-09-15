using System.Text.Json;

namespace Heartbeat.Dev;

internal sealed record FunctionMetric(
    string Language,
    string Path,
    int Line,
    string Symbol,
    int Complexity,
    int Lines)
{
    public long Burden => (long)Complexity * Lines;
}

internal static class ErosionScanner
{
    public static IReadOnlyList<FunctionMetric> FromCSharpAnalyzer(
        string root,
        IReadOnlyList<ComplexityHotspot> functions) =>
        functions.Select(function => new FunctionMetric(
            function.Language,
            function.Path,
            function.Line,
            function.Symbol,
            function.Complexity,
            MeasureCSharpFunction(root, function.Path, function.Line)))
        .ToArray();

    public static IReadOnlyList<FunctionMetric> ParseTypeScriptSpans(string json) =>
        JsonSerializer.Deserialize<FunctionMetric[]>(json, JsonOptions.Indented) ?? [];

    private static int MeasureCSharpFunction(string root, string relativePath, int startLine)
    {
        var lines = File.ReadAllLines(Path.Combine(root, relativePath));
        var startIndex = Math.Max(0, startLine - 1);
        var endIndex = FindCSharpEnd(lines, startIndex);
        var text = string.Join(Environment.NewLine, lines[startIndex..(endIndex + 1)]);
        return Math.Max(1, SourceLineCounter.Count(text, "C#"));
    }

    private static int FindCSharpEnd(string[] lines, int start)
    {
        var blockDepth = 0;
        var foundBody = false;
        var expressionBody = false;
        var inBlockComment = false;
        var inString = false;
        var verbatim = false;
        var quote = '\0';
        var escaped = false;
        for (var lineIndex = start; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex];
            for (var index = 0; index < line.Length; index++)
            {
                var character = line[index];
                var next = index + 1 < line.Length ? line[index + 1] : '\0';
                if (inBlockComment)
                {
                    if (character == '*' && next == '/')
                    {
                        inBlockComment = false;
                        index++;
                    }
                    continue;
                }
                if (inString)
                {
                    if (!verbatim && escaped) escaped = false;
                    else if (!verbatim && character == '\\') escaped = true;
                    else if (verbatim && character == '"' && next == '"') index++;
                    else if (character == quote) inString = false;
                    continue;
                }
                if (character == '/' && next == '/') break;
                if (character == '/' && next == '*')
                {
                    inBlockComment = true;
                    index++;
                    continue;
                }
                if (character is '"' or '\'')
                {
                    inString = true;
                    verbatim = character == '"' && index > 0 && line[index - 1] == '@';
                    quote = character;
                    continue;
                }
                if (!foundBody && character == '=' && next == '>') expressionBody = true;
                if (!expressionBody && character == '{')
                {
                    foundBody = true;
                    blockDepth++;
                }
                else if (foundBody && character == '}' && --blockDepth == 0) return lineIndex;
                else if (expressionBody && blockDepth == 0 && character == ';') return lineIndex;
            }
        }
        return lines.Length - 1;
    }
}
