using System.Text.Json;
using System.Text.RegularExpressions;

namespace Heartbeat.Dev;

internal sealed record DeadCodeFinding(string Rule, string Path, int Line, string Symbol)
{
    internal string Identity => $"{Rule}\0{Path}\0{Symbol}";
    public string Describe() => $"{Path}:{Line} {Rule}: {Symbol}";
}

internal sealed record DeadCodeReport(
    bool Available, string? Reason,
    IReadOnlyList<DeadCodeFinding> Current, IReadOnlyList<DeadCodeFinding> NewFindings)
{
    public IEnumerable<string> Failures(bool stock)
    {
        if (!Available) yield return Reason!;
        else if (!stock && NewFindings.Count > 0)
            yield return "New unused code or dependency findings:" + Environment.NewLine
                + string.Join(Environment.NewLine, NewFindings.Select(item => "    " + item.Describe()));
    }
}

// Knip resolves JS/TS entry points; Roslyn supplies unused-private-member diagnostics
// from the existing metrics build. Neither scan proves that a feature has business value.
internal sealed partial class DeadCodeDetector(RepositoryContext repository, IProcessRunner runner)
{
    public async Task<DeadCodeReport> CompareAsync(string baseCommit, string directory,
        ICollection<string> commands, CancellationToken cancellationToken)
    {
        var (baseline, error) = await new BaselineWorkspaceCache(repository, runner)
            .PrepareAsync(baseCommit, commands, cancellationToken);
        if (baseline is null) return new(false, error, [], []);
        try
        {
            var before = await ScanAsync(baseline.Path, "baseline", directory, commands, cancellationToken);
            var current = await ScanAsync(repository.Root, "current", directory, commands, cancellationToken);
            return new(true, null, current, FindNew(before, current));
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException)
        {
            return new(false, exception.Message, [], []);
        }
    }

    private async Task<IReadOnlyList<DeadCodeFinding>> ScanAsync(string root, string label, string directory,
        ICollection<string> commands, CancellationToken cancellationToken)
    {
        var web = Path.Combine(root, "src", "Frontend", "Heartbeat.Web");
        var currentWeb = repository.Path("src", "Frontend", "Heartbeat.Web");
        var executable = Path.Combine(currentWeb, "node_modules", ".bin", OperatingSystem.IsWindows() ? "knip.cmd" : "knip");
        if (!File.Exists(executable)) throw new InvalidDataException($"Knip is missing; run npm --prefix {currentWeb} ci.");
        // Use the same scanner and entry-point configuration for both revisions.
        string[] arguments = ["--directory", web, "--config", Path.Combine(currentWeb, "knip.json"), "--reporter", "json", "--no-progress"];
        commands.Add("knip " + string.Join(' ', arguments));
        var result = await ProcessRunner.CaptureAsync(web, executable, arguments, cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(directory, label + "-knip.json"), result.StdOut, cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(directory, label + "-knip.log"), result.StdErr, cancellationToken);
        if (result.ExitCode is not (0 or 1)) throw new InvalidDataException($"Knip failed ({label}): {result.StdErr}");
        var findings = ParseKnip(result.StdOut);
        if (result.ExitCode == 1 && findings.Count == 0)
            throw new InvalidDataException($"Knip failed without findings ({label}): {result.StdErr}");
        var csharp = await File.ReadAllTextAsync(Path.Combine(directory, label + "-csharp.log"), cancellationToken);
        return [.. findings, .. ParseCSharp(root, csharp)];
    }

    internal static IReadOnlyList<DeadCodeFinding> ParseKnip(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("issues", out var issues) || issues.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Knip did not produce an issues array.");
        var findings = new List<DeadCodeFinding>();
        foreach (var issue in issues.EnumerateArray())
        {
            var path = "src/Frontend/Heartbeat.Web/" + issue.GetProperty("file").GetString();
            foreach (var category in issue.EnumerateObject().Where(item => item.Value.ValueKind == JsonValueKind.Array))
                foreach (var item in category.Value.EnumerateArray())
                    findings.Add(new("knip/" + category.Name, path,
                        item.TryGetProperty("line", out var line) ? line.GetInt32() : 1,
                        item.GetProperty("name").GetString()!));
        }
        return findings;
    }

    internal static IReadOnlyList<DeadCodeFinding> ParseCSharp(string root, string output) =>
        output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => UnusedMember().Match(line)).Where(match => match.Success)
            .Select(match => new DeadCodeFinding("IDE0051",
                Path.GetRelativePath(root, match.Groups["path"].Value).Replace('\\', '/'),
                int.Parse(match.Groups["line"].Value, System.Globalization.CultureInfo.InvariantCulture),
                match.Groups["symbol"].Value))
            .Where(item => SourceCorpus.TryClassify(item.Path, out _, out var role) && role == SourceRole.Production)
            .DistinctBy(item => item.Identity).ToArray();

    internal static IReadOnlyList<DeadCodeFinding> FindNew(
        IReadOnlyList<DeadCodeFinding> baseline, IReadOnlyList<DeadCodeFinding> current)
    {
        var known = baseline.Select(item => item.Identity).ToHashSet(StringComparer.Ordinal);
        return current.Where(item => !known.Contains(item.Identity)).ToArray();
    }

    [GeneratedRegex("^(?<path>.+)\\((?<line>\\d+),\\d+\\): (?:warning|error) IDE0051: Private member '(?<symbol>[^']+)' is unused", RegexOptions.CultureInvariant)]
    private static partial Regex UnusedMember();
}
