using System.Globalization;
using System.Text.RegularExpressions;

namespace Heartbeat.Dev;

internal sealed record CouplingHotspot(string Path, int Line, string Symbol, int TypeCount);

internal sealed record CouplingObservation(
    IReadOnlyList<CouplingHotspot> Baseline,
    IReadOnlyList<CouplingHotspot> Current);

internal static partial class CouplingParser
{
    public static IReadOnlyList<CouplingHotspot> Parse(string root, string output) =>
        output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => Diagnostic().Match(line))
            .Where(match => match.Success)
            .Select(match => new CouplingHotspot(
                Path.GetRelativePath(root, match.Groups["path"].Value).Replace('\\', '/'),
                int.Parse(match.Groups["line"].Value, CultureInfo.InvariantCulture),
                match.Groups["symbol"].Value,
                int.Parse(match.Groups["types"].Value, CultureInfo.InvariantCulture)))
            .Where(item => SourceCorpus.TryClassify(item.Path, out _, out var role) && role == SourceRole.Production)
            .Distinct().OrderBy(item => item.Path, StringComparer.Ordinal).ThenBy(item => item.Line).ToArray();

    [GeneratedRegex("^(?<path>.+)\\((?<line>\\d+),\\d+\\): warning CA1506: '(?<symbol>[^']+)' is coupled with '(?<types>\\d+)' different types", RegexOptions.CultureInvariant)]
    private static partial Regex Diagnostic();
}
