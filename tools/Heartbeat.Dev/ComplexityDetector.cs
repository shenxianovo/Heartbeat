using System.Text.Json;
using System.Text.RegularExpressions;

namespace Heartbeat.Dev;

internal sealed record ComplexityHotspot(string Language, string Path, int Line, string Symbol, int Complexity);

internal sealed record ComplexityRegression(ComplexityHotspot Current, int? BaseComplexity);

internal sealed record ErosionMeasurement(
    int TotalFunctions,
    int HighRiskFunctions,
    long TotalBurden,
    long HighRiskBurden,
    double Ratio);

internal sealed record ComplexityQualityReport(
    bool Available,
    bool Passed,
    string? Reason,
    int CurrentHotspots,
    int Regressions,
    string? ReportPath,
    ErosionMeasurement? BaselineErosion = null,
    ErosionMeasurement? CurrentErosion = null,
    double? ErosionDelta = null,
    IReadOnlyList<ComplexityHotspot>? Hotspots = null,
    IReadOnlyList<ComplexityRegression>? RegressionDetails = null);

internal sealed partial class ComplexityDetector(RepositoryContext repository, IProcessRunner runner)
{
    private const int Threshold = 10;

    public async Task<ComplexityQualityReport> CompareAsync(
        string baseRef,
        string artifactDirectory,
        ICollection<string> commands,
        ICollection<string> artifacts,
        CancellationToken cancellationToken)
    {
        var temporaryRoot = OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath();
        var worktree = Path.Combine(temporaryRoot, $"heartbeat-quality-{Guid.NewGuid():N}");
        try
        {
            var added = await runner.CaptureAsync(
                "git", ["worktree", "add", "--detach", worktree, baseRef], null, cancellationToken);
            if (added.ExitCode != 0)
            {
                return Unavailable($"Could not materialize Git base '{baseRef}': {LastUsefulLine(added)}");
            }

            commands.Add($"git worktree add --detach <temporary> {baseRef}");
            var baseline = await ScanAsync(worktree, restore: true, commands, cancellationToken);
            var current = await ScanAsync(repository.Root, restore: false, commands, cancellationToken);
            if (baseline.Error is not null || current.Error is not null)
            {
                return Unavailable(baseline.Error ?? current.Error!);
            }

            var regressions = FindRegressions(baseline.Hotspots, current.Hotspots);
            var baselineErosion = CalculateErosion(baseline.Functions);
            var currentErosion = CalculateErosion(current.Functions);
            var reportPath = Path.Combine(artifactDirectory, "complexity.json");
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new
            {
                threshold = Threshold,
                baseline = baseline.Hotspots,
                current = current.Hotspots,
                regressions,
                baselineErosion,
                currentErosion,
                erosionDelta = currentErosion.Ratio - baselineErosion.Ratio,
                currentHighRiskFunctions = current.Functions.Where(item => item.Complexity > Threshold),
            }, JsonOptions.Indented) + Environment.NewLine, cancellationToken);
            artifacts.Add("complexity.json");
            return new ComplexityQualityReport(
                true,
                regressions.Count == 0,
                regressions.Count == 0 ? null : "One or more production methods crossed complexity 10 or grew above it.",
                current.Hotspots.Count,
                regressions.Count,
                reportPath,
                baselineErosion,
                currentErosion,
                currentErosion.Ratio - baselineErosion.Ratio,
                current.Hotspots,
                regressions);
        }
        finally
        {
            if (Directory.Exists(worktree))
            {
                await runner.CaptureAsync(
                    "git", ["worktree", "remove", "--force", worktree], null, CancellationToken.None);
            }
        }
    }

    private async Task<ComplexityScan> ScanAsync(
        string root,
        bool restore,
        ICollection<string> commands,
        CancellationToken cancellationToken)
    {
        var csharp = await ScanCSharpAsync(root, restore, commands, cancellationToken);
        if (csharp.Error is not null) return csharp;
        var typescript = await ScanTypeScriptAsync(root, commands, cancellationToken);
        return typescript.Error is not null
            ? typescript
            : new ComplexityScan(
                [.. csharp.Hotspots, .. typescript.Hotspots],
                [.. csharp.Functions, .. typescript.Functions],
                null);
    }

    private async Task<ComplexityScan> ScanCSharpAsync(
        string root,
        bool restore,
        ICollection<string> commands,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string>
        {
            "build", Path.Combine(root, "Heartbeat.slnx"), "--no-incremental", "--disable-build-servers",
            "--verbosity", "minimal", "--maxcpucount:1",
            "-p:TreatWarningsAsErrors=false", "-p:WarningsAsErrors=",
            $"-p:CustomBeforeMicrosoftCommonProps={repository.Path("tools", "Heartbeat.Dev", "CodeMetrics.props")}",
        };
        if (!restore) arguments.Insert(2, "--no-restore");
        commands.Add($"dotnet build Heartbeat.slnx{(restore ? string.Empty : " --no-restore")} (CA1502)");
        var result = await ProcessRunner.CaptureAsync(root, "dotnet", arguments, cancellationToken);
        if (result.ExitCode != 0)
        {
            return new ComplexityScan([], [],
                $"C# complexity scan failed with exit code {result.ExitCode}: {FailureSummary(result)}");
        }
        try
        {
            var functions = ParseCSharp(root, result.StdOut + Environment.NewLine + result.StdErr);
            return new ComplexityScan(
                functions.Where(item => item.Complexity > Threshold).ToArray(),
                ErosionScanner.FromCSharpAnalyzer(root, functions),
                null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new ComplexityScan([], [], $"C# erosion scan failed: {exception.Message}");
        }
    }

    private async Task<ComplexityScan> ScanTypeScriptAsync(
        string root,
        ICollection<string> commands,
        CancellationToken cancellationToken)
    {
        var web = Path.Combine(root, "src", "Frontend", "Heartbeat.Web");
        if (!Directory.Exists(web)) return new ComplexityScan([], [], null);
        if (!string.Equals(root, repository.Root, StringComparison.Ordinal))
        {
            commands.Add("npm ci --ignore-scripts (temporary Git base)");
            var installed = await ProcessRunner.CaptureAsync(
                web, "npm", ["ci", "--ignore-scripts"], cancellationToken);
            if (installed.ExitCode != 0)
            {
                return new ComplexityScan([], [], $"Could not install the baseline TypeScript scanner: {LastUsefulLine(installed)}");
            }
        }
        var executable = OperatingSystem.IsWindows()
            ? Path.Combine(web, "node_modules", ".bin", "eslint.cmd")
            : Path.Combine(web, "node_modules", ".bin", "eslint");
        if (!File.Exists(executable))
        {
            return new ComplexityScan([], [], $"TypeScript complexity scanner is missing. Run npm --prefix {web} ci");
        }
        var target = Path.Combine(web, "src");
        if (!Directory.Exists(target)) return new ComplexityScan([], [], null);

        commands.Add("eslint src/Frontend/Heartbeat.Web/src --rule complexity:[warn,0] --format json");
        var result = await ProcessRunner.CaptureAsync(root, executable,
            [target, "--config", Path.Combine(web, "eslint.config.mjs"), "--rule", "complexity: [\"warn\", 0]", "--format", "json"],
            cancellationToken);
        if (result.ExitCode is not (0 or 1))
        {
            return new ComplexityScan([], [], $"TypeScript complexity scan failed: {LastUsefulLine(result)}");
        }
        try
        {
            commands.Add("node tools/Heartbeat.Dev/erosion-typescript.mjs <source-root>");
            var erosion = await ProcessRunner.CaptureAsync(
                root,
                "node",
                [repository.Path("tools", "Heartbeat.Dev", "erosion-typescript.mjs"), root],
                cancellationToken);
            if (erosion.ExitCode != 0)
            {
                return new ComplexityScan([], [], $"TypeScript erosion scan failed: {LastUsefulLine(erosion)}");
            }
            var functions = ParseTypeScript(root, result.StdOut);
            return new ComplexityScan(
                functions.Where(item => item.Complexity > Threshold).ToArray(),
                ApplyAnalyzerComplexity(ErosionScanner.ParseTypeScriptSpans(erosion.StdOut), functions),
                null);
        }
        catch (JsonException exception)
        {
            return new ComplexityScan([], [], $"Could not parse TypeScript complexity output: {exception.Message}");
        }
    }

    internal static IReadOnlyList<ComplexityHotspot> ParseCSharp(string root, string output) =>
        output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => CSharpDiagnostic().Match(line))
            .Where(match => match.Success)
            .Select(match => new ComplexityHotspot(
                "C#", Relative(root, match.Groups["path"].Value),
                int.Parse(match.Groups["line"].Value, System.Globalization.CultureInfo.InvariantCulture),
                match.Groups["symbol"].Value,
                int.Parse(match.Groups["complexity"].Value, System.Globalization.CultureInfo.InvariantCulture)))
            .Where(IsProduction)
            .Distinct()
            .OrderBy(hotspot => hotspot.Path, StringComparer.Ordinal)
            .ThenBy(hotspot => hotspot.Line)
            .ToArray();

    internal static IReadOnlyList<ComplexityHotspot> ParseTypeScript(string root, string json)
    {
        using var document = JsonDocument.Parse(json);
        var hotspots = new List<ComplexityHotspot>();
        foreach (var file in document.RootElement.EnumerateArray())
        {
            var path = Relative(root, file.GetProperty("filePath").GetString()!);
            foreach (var message in file.GetProperty("messages").EnumerateArray())
            {
                if (message.GetProperty("ruleId").GetString() != "complexity") continue;
                var text = message.GetProperty("message").GetString()!;
                var match = ComplexityNumber().Match(text);
                if (!match.Success) continue;
                var separator = text.IndexOf(" has a complexity", StringComparison.Ordinal);
                hotspots.Add(new ComplexityHotspot(
                    "TypeScript", path, message.GetProperty("line").GetInt32(),
                    separator < 0 ? text : text[..separator],
                    int.Parse(match.Groups["complexity"].Value, System.Globalization.CultureInfo.InvariantCulture)));
            }
        }
        return hotspots.OrderBy(item => item.Path, StringComparer.Ordinal).ThenBy(item => item.Line).ToArray();
    }

    internal static IReadOnlyList<ComplexityRegression> FindRegressions(
        IReadOnlyList<ComplexityHotspot> baseline,
        IReadOnlyList<ComplexityHotspot> current)
    {
        var previous = baseline.ToDictionary(Identity, item => item.Complexity, StringComparer.Ordinal);
        return current
            .Select(item => new ComplexityRegression(
                item,
                previous.TryGetValue(Identity(item), out var complexity) ? complexity : null))
            .Where(item => item.BaseComplexity is null || item.Current.Complexity > item.BaseComplexity)
            .ToArray();
    }

    internal static ErosionMeasurement CalculateErosion(IReadOnlyList<FunctionMetric> functions)
    {
        var totalBurden = functions.Sum(item => item.Burden);
        var highRisk = functions.Where(item => item.Complexity > Threshold).ToArray();
        var highRiskBurden = highRisk.Sum(item => item.Burden);
        return new ErosionMeasurement(
            functions.Count,
            highRisk.Length,
            totalBurden,
            highRiskBurden,
            totalBurden == 0 ? 0 : (double)highRiskBurden / totalBurden);
    }

    internal static IReadOnlyList<FunctionMetric> ApplyAnalyzerComplexity(
        IReadOnlyList<FunctionMetric> functions,
        IReadOnlyList<ComplexityHotspot> hotspots)
    {
        var analyzerValues = hotspots
            .GroupBy(item => $"{item.Language}\0{item.Path}\0{item.Line}", StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Max(item => item.Complexity), StringComparer.Ordinal);
        return functions.Select(function =>
        {
            var key = $"{function.Language}\0{function.Path}\0{function.Line}";
            return analyzerValues.TryGetValue(key, out var complexity)
                ? function with { Complexity = complexity }
                : function;
        }).ToArray();
    }

    private static bool IsProduction(ComplexityHotspot hotspot) =>
        SourceCorpus.TryClassify(hotspot.Path, out _, out var role) && role == SourceRole.Production;

    private static string Identity(ComplexityHotspot hotspot) =>
        $"{hotspot.Language}\0{hotspot.Path}\0{hotspot.Symbol}";

    private static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');

    private static ComplexityQualityReport Unavailable(string reason) => new(false, false, reason, 0, 0, null);

    private static string LastUsefulLine(ProcessResult result) =>
        (result.StdErr + Environment.NewLine + result.StdOut)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault()?.Trim() ?? "command failed";

    private static string FailureSummary(ProcessResult result)
    {
        var lines = (result.StdErr + Environment.NewLine + result.StdOut)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var errors = lines.Where(line => line.Contains(": error ", StringComparison.OrdinalIgnoreCase))
            .TakeLast(3)
            .Select(line => line.Trim())
            .ToArray();
        return errors.Length > 0 ? string.Join(" | ", errors) : LastUsefulLine(result);
    }

    [GeneratedRegex("^(?<path>.+)\\((?<line>\\d+),\\d+\\): warning CA1502: '(?<symbol>[^']+)' has a cyclomatic complexity of '(?<complexity>\\d+)'", RegexOptions.CultureInvariant)]
    private static partial Regex CSharpDiagnostic();

    [GeneratedRegex("complexity of (?<complexity>\\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex ComplexityNumber();

    private sealed record ComplexityScan(
        IReadOnlyList<ComplexityHotspot> Hotspots,
        IReadOnlyList<FunctionMetric> Functions,
        string? Error);
}
