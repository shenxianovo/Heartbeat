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
    public IEnumerable<string> Failures()
    {
        if (!Available) yield return Reason!;
    }
}

// Knip resolves JS/TS entry points; Roslyn supplies unused-private-member diagnostics
// from the existing metrics build. Neither scan proves that a feature has business value.
internal sealed partial class DeadCodeDetector(RepositoryContext repository, IProcessRunner runner)
{
    public async Task<DeadCodeReport> CompareAsync(string baseCommit, string directory,
        ICollection<string> commands, bool baselineCSharpComplete, bool currentCSharpComplete, CancellationToken cancellationToken)
    {
        var (baseline, error) = await new BaselineWorkspaceCache(repository, runner)
            .PrepareAsync(baseCommit, commands, cancellationToken);
        if (baseline is null) return new(false, error, [], []);
        try
        {
            var before = await ScanAsync(baseline.Path, "baseline", directory, commands, baselineCSharpComplete, cancellationToken);
            var current = await ScanAsync(repository.Root, "current", directory, commands, currentCSharpComplete, cancellationToken);
            var complete = baselineCSharpComplete && currentCSharpComplete;
            var newFindings = FindNew(before, current).Where(item => complete || item.Rule != "IDE0051").ToArray();
            return new(complete, complete ? null : "C# unused-member comparison requires successful baseline and current builds.", current, newFindings);
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException)
        {
            return new(false, exception.Message, [], []);
        }
    }

    private async Task<IReadOnlyList<DeadCodeFinding>> ScanAsync(string root, string label, string directory,
        ICollection<string> commands, bool csharpComplete, CancellationToken cancellationToken)
    {
        var findings = new List<DeadCodeFinding>();
        await ScanKnipAsync("src/Docs/Heartbeat.Docs", "knip-docs.json", "docs");
        await ScanKnipAsync("tools/Heartbeat.Dev/jscpd", "knip-tools.json", "tools");
        if (csharpComplete)
        {
            var csharp = await File.ReadAllTextAsync(Path.Combine(directory, label + "-csharp.log"), cancellationToken);
            findings.AddRange(ParseCSharp(root, csharp));
        }
        return findings;

        async Task ScanKnipAsync(string relative, string config, string scope)
        {
            var project = Path.Combine(root, relative);
            if (!Directory.Exists(project)) return;
            var tools = repository.Path("tools", "Heartbeat.Dev", "jscpd");
            var executable = Path.Combine(tools, "node_modules", ".bin", OperatingSystem.IsWindows() ? "knip.cmd" : "knip");
            if (!File.Exists(executable)) throw new InvalidDataException($"Knip is missing; run npm --prefix {tools} ci --ignore-scripts.");
            if (scope == "docs" && !Directory.Exists(Path.Combine(project, "node_modules")))
            {
                commands.Add($"pnpm install --frozen-lockfile --ignore-scripts ({label} Docs)");
                var restored = await ProcessRunner.CaptureAsync(project, "pnpm",
                    ["install", "--frozen-lockfile", "--ignore-scripts"], cancellationToken);
                if (restored.ExitCode != 0) throw new InvalidDataException($"Could not prepare {label} Docs dependencies: {restored.StdErr}");
            }
            string[] arguments = ["--directory", project, "--config", Path.Combine(tools, config), "--reporter", "json", "--no-progress"];
            commands.Add("knip " + string.Join(' ', arguments));
            var result = await ProcessRunner.CaptureAsync(project, executable, arguments, cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(directory, $"{label}-{scope}-knip.json"), result.StdOut, cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(directory, $"{label}-{scope}-knip.log"), result.StdErr, cancellationToken);
            if (result.ExitCode is not (0 or 1)) throw new InvalidDataException($"Knip failed ({label} {scope}): {result.StdErr}");
            var scanned = ParseKnip(result.StdOut, relative);
            if (result.ExitCode == 1 && scanned.Count == 0)
                throw new InvalidDataException($"Knip failed without findings ({label} {scope}): {result.StdErr}");
            findings.AddRange(scanned);
        }
    }

    internal static IReadOnlyList<DeadCodeFinding> ParseKnip(string json, string prefix = "src/Docs/Heartbeat.Docs")
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("issues", out var issues) || issues.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Knip did not produce an issues array.");
        var findings = new List<DeadCodeFinding>();
        foreach (var issue in issues.EnumerateArray())
        {
            var path = prefix + "/" + issue.GetProperty("file").GetString();
            foreach (var category in issue.EnumerateObject().Where(item => item.Value.ValueKind == JsonValueKind.Array))
                foreach (var item in category.Value.EnumerateArray())
                {
                    var entries = item.ValueKind == JsonValueKind.Array ? item.EnumerateArray().ToArray() : [item];
                    foreach (var entry in entries)
                        findings.Add(new("knip/" + category.Name, path,
                            entry.TryGetProperty("line", out var line) ? line.GetInt32() : 1,
                            entry.TryGetProperty("name", out var name) ? name.GetString()! : path));
                }
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
            .Where(item => SourceCorpus.TryClassify(item.Path, out _, out var role) && role is SourceRole.Production or SourceRole.Tooling)
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
