using System.Text.Json;
using System.Text.RegularExpressions;

namespace Heartbeat.Dev;

internal sealed record ComplexityHotspot(string Language, string Path, int Line, string Symbol, int Complexity, int Column = 1);

internal sealed record ComplexityRegression(ComplexityHotspot Current, int? BaseComplexity);
internal sealed record ComplexityModuleObservation(string Module, ErosionMeasurement? Baseline, ErosionMeasurement Current);

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
    IReadOnlyList<ComplexityRegression>? RegressionDetails = null,
    CouplingObservation? Coupling = null,
    bool BaselineComplete = true,
    bool CurrentComplete = true,
    bool BaselineCSharpComplete = true,
    bool CurrentCSharpComplete = true,
    IReadOnlyList<ComplexityModuleObservation>? Modules = null);

internal sealed partial class ComplexityDetector(RepositoryContext repository, IProcessRunner runner)
{
    private const int Threshold = 10;

    public async Task<ComplexityQualityReport> CompareAsync(
        string baseRef,
        string artifactDirectory,
        ICollection<string> commands,
        CancellationToken cancellationToken)
    {
        var (workspace, error) = await new BaselineWorkspaceCache(repository, runner)
            .PrepareAsync(baseRef, commands, cancellationToken);
        if (workspace is null)
        {
            return Unavailable(error!);
        }

        var baseline = await ScanAsync(
            workspace.Path, workspace.NeedsRestore, artifactDirectory, commands, cancellationToken);
        var current = await ScanAsync(repository.Root, restore: true, artifactDirectory, commands, cancellationToken);
        if (baseline.Error is null) workspace.MarkRestored();
        var completed = baseline.Error is null && current.Error is null;
        var reason = string.Join(Environment.NewLine, new[] { baseline.Error, current.Error }.Where(error => error is not null));
        var regressions = completed ? FindRegressions(baseline.Hotspots, current.Hotspots) : [];
        var baselineErosion = CalculateErosion(baseline.Functions);
        var currentErosion = CalculateErosion(current.Functions);
        var modules = current.Functions.GroupBy(function => Module(function.Path), StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new ComplexityModuleObservation(group.Key,
                completed ? CalculateErosion(baseline.Functions.Where(function => Module(function.Path) == group.Key).ToArray()) : null,
                CalculateErosion(group.ToArray()))).ToArray();
        var coupling = new CouplingObservation(baseline.Coupling ?? [], current.Coupling ?? []);
        var reportPath = Path.Combine(artifactDirectory, "complexity.json");
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new
        {
            threshold = Threshold,
            completed,
            reason,
            baselineComplete = baseline.Error is null,
            currentComplete = current.Error is null,
            modules,
            coupling = completed ? coupling : null,
            baseline = baseline.Hotspots,
            current = current.Hotspots,
            regressions,
            baselineErosion = baseline.Error is null ? baselineErosion : null,
            currentErosion = current.Error is null ? currentErosion : null,
            erosionDelta = completed ? (double?)(currentErosion.Ratio - baselineErosion.Ratio) : null,
            currentHighRiskFunctions = current.Functions.Where(item => item.Complexity > Threshold),
        }, JsonOptions.Indented) + Environment.NewLine, cancellationToken);
        return new ComplexityQualityReport(
            completed,
            completed,
            completed ? null : reason,
            current.Hotspots.Count,
            regressions.Count,
            reportPath,
            baseline.Error is null ? baselineErosion : null,
            current.Error is null ? currentErosion : null,
            completed ? currentErosion.Ratio - baselineErosion.Ratio : null,
            current.Hotspots,
            regressions,
            completed ? coupling : null,
            baseline.Error is null,
            current.Error is null,
            baseline.CSharpComplete,
            current.CSharpComplete,
            modules);
    }

    private async Task<ComplexityScan> ScanAsync(
        string root,
        bool restore,
        string artifactDirectory,
        ICollection<string> commands,
        CancellationToken cancellationToken)
    {
        var csharp = await ScanCSharpAsync(root, restore, artifactDirectory, commands, cancellationToken);
        var typescript = await ScanTypeScriptAsync(root, artifactDirectory, commands, cancellationToken);
        var errors = new[] { csharp.Error, typescript.Error }.Where(error => error is not null).ToArray();
        return new ComplexityScan(
            [.. csharp.Hotspots, .. typescript.Hotspots],
            [.. csharp.Functions, .. typescript.Functions],
            errors.Length == 0 ? null : string.Join(Environment.NewLine, errors), csharp.Coupling, csharp.Error is null);
    }

    private async Task<ComplexityScan> ScanCSharpAsync(
        string root,
        bool restore,
        string artifactDirectory,
        ICollection<string> commands,
        CancellationToken cancellationToken)
    {
        var label = string.Equals(root, repository.Root, StringComparison.Ordinal) ? "current" : "baseline";
        var result = await CSharpAnalysisBuild.RunAsync(root, restore, artifactDirectory, label,
            repository.Path("tools", "Heartbeat.Dev", "CodeMetrics.props"), commands, cancellationToken);
        var buildOutput = result.StdOut + Environment.NewLine + result.StdErr;
        await File.WriteAllTextAsync(Path.Combine(artifactDirectory, label + "-csharp.log"),
            buildOutput, cancellationToken);
        if (result.ExitCode != 0)
        {
            return new ComplexityScan([], [],
                $"C# complexity scan failed with exit code {result.ExitCode}: {FailureSummary(result)}");
        }
        try
        {
            var all = ParseAllCSharp(root, buildOutput);
            var production = all.Where(IsImplementation).ToArray();
            var functions = ErosionScanner.FromCSharpAnalyzer(root, production);
            if (functions.Count == 0)
                return new ComplexityScan([], [], NoProductionMetrics(root, all));
            return new ComplexityScan(
                functions.Where(item => item.Complexity > Threshold).Select(item => item.Hotspot).ToArray(),
                functions,
                null, CouplingParser.Parse(root, buildOutput));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new ComplexityScan([], [], $"C# erosion scan failed: {exception.Message}");
        }
    }

    /// <summary>
    /// 未测到实现函数时，用诊断数量和源码位置说明基点与扫描范围是否匹配。
    /// </summary>
    internal static string NoProductionMetrics(string root, IReadOnlyList<ComplexityHotspot> all)
    {
        if (all.Count == 0)
        {
            return "CA1502 produced no function metrics at all; check that the analyzer package is available "
                + "(tools/Heartbeat.Dev/CodeMetrics.props enables CA1502) and that the build actually compiled C# projects.";
        }

        var roots = all.Select(hotspot => hotspot.Path.Split('/')[0])
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return $"CA1502 measured {all.Count} C# functions at '{root}', but none of them classify as implementation code "
            + $"(they live under {string.Join(", ", roots.Select(item => item + "/"))}; implementation is measured under src/ and tools/). "
            + "Choose a Git base containing the current src/ and tools/ layout.";
    }

    private async Task<ComplexityScan> ScanTypeScriptAsync(
        string root,
        string artifactDirectory,
        ICollection<string> commands,
        CancellationToken cancellationToken)
    {
        var tools = repository.Path("tools", "Heartbeat.Dev", "jscpd");
        var executable = Path.Combine(tools, "node_modules", ".bin", OperatingSystem.IsWindows() ? "eslint.cmd" : "eslint");
        if (!File.Exists(executable))
            return new ComplexityScan([], [], $"TypeScript scanner is missing; run npm --prefix {tools} ci --ignore-scripts.");
        var source = new GitSourceReader(new RepositoryContext(root), new ProcessRunner(root));
        var snapshot = await source.ReadWorktreeAsync(cancellationToken);
        var files = snapshot.Files.Where(file => file.Role is SourceRole.Production or SourceRole.Tooling
            && file.Language is "TypeScript" or "JavaScript").Select(file => file.Path).ToArray();
        if (files.Length == 0) return new ComplexityScan([], [], null);
        var label = root == repository.Root ? "current" : "baseline";
        var directory = artifactDirectory;
        Directory.CreateDirectory(directory);
        var listPath = Path.Combine(directory, label + "-files.json");
        await File.WriteAllTextAsync(listPath, JsonSerializer.Serialize(files), cancellationToken);
        commands.Add($"eslint {label} implementation files (complexity)");
        var result = await ProcessRunner.CaptureAsync(root, executable,
            ["--config", Path.Combine(tools, "eslint.config.mjs"), "--no-ignore", "--format", "json", .. files], cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(directory, label + "-typescript.json"), result.StdOut, cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(directory, label + "-typescript.log"), result.StdErr, cancellationToken);
        if (result.ExitCode is not (0 or 1))
            return new ComplexityScan([], [], $"TypeScript complexity scan failed: {LastUsefulLine(result)}");
        try
        {
            commands.Add("node tools/Heartbeat.Dev/erosion-typescript.mjs <source-root>");
            var erosion = await ProcessRunner.CaptureAsync(
                root,
                "node",
                [repository.Path("tools", "Heartbeat.Dev", "erosion-typescript.mjs"), root, listPath],
                cancellationToken);
            if (erosion.ExitCode != 0)
            {
                return new ComplexityScan([], [], $"TypeScript erosion scan failed: {LastUsefulLine(erosion)}");
            }
            var functions = ApplyAnalyzerComplexity(ErosionScanner.ParseTypeScriptSpans(erosion.StdOut), ParseTypeScript(root, result.StdOut));
            return new ComplexityScan(
                functions.Where(item => item.Complexity > Threshold).Select(item => item.Hotspot).ToArray(),
                functions,
                null);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            return new ComplexityScan([], [], $"Could not parse TypeScript complexity output: {exception.Message}");
        }
    }

    internal static IReadOnlyList<ComplexityHotspot> ParseCSharp(string root, string output) =>
        [.. ParseAllCSharp(root, output).Where(IsImplementation)];

    /// 保留所有角色的 CA1502 诊断，用数量与路径判断基点是否有效。
    internal static IReadOnlyList<ComplexityHotspot> ParseAllCSharp(string root, string output) =>
        output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => CSharpDiagnostic().Match(line))
            .Where(match => match.Success)
            .Select(match => new ComplexityHotspot(
                "C#", Relative(root, match.Groups["path"].Value),
                int.Parse(match.Groups["line"].Value, System.Globalization.CultureInfo.InvariantCulture),
                match.Groups["symbol"].Value,
                int.Parse(match.Groups["complexity"].Value, System.Globalization.CultureInfo.InvariantCulture),
                int.Parse(match.Groups["column"].Value, System.Globalization.CultureInfo.InvariantCulture)))
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
            if (!SourceCorpus.TryClassify(path, out _, out var role) || role is not (SourceRole.Production or SourceRole.Tooling)) continue;
            foreach (var message in file.GetProperty("messages").EnumerateArray())
            {
                if (message.TryGetProperty("fatal", out var fatal) && fatal.GetBoolean()
                    || message.TryGetProperty("severity", out var severity) && severity.GetInt32() > 1)
                    throw new InvalidDataException($"TypeScript analysis failed at {path}: {message.GetProperty("message").GetString()}");
                if (message.GetProperty("ruleId").GetString() != "complexity") continue;
                var text = message.GetProperty("message").GetString()!;
                var match = ComplexityNumber().Match(text);
                if (!match.Success) continue;
                var separator = text.IndexOf(" has a complexity", StringComparison.Ordinal);
                hotspots.Add(new ComplexityHotspot(
                    "TypeScript", path, message.GetProperty("line").GetInt32(),
                    separator < 0 ? text : text[..separator],
                    int.Parse(match.Groups["complexity"].Value, System.Globalization.CultureInfo.InvariantCulture),
                    message.GetProperty("column").GetInt32()));
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
        var measured = new Dictionary<FunctionMetric, int>();
        foreach (var hotspot in hotspots)
        {
            var kind = hotspot.Symbol.StartsWith("Class field initializer", StringComparison.Ordinal) ? "initializer"
                : hotspot.Symbol.StartsWith("Class static block", StringComparison.Ordinal) ? "static-block" : "function";
            var function = functions.Where(item => item.Path == hotspot.Path && item.Kind == kind && Contains(item, hotspot))
                .OrderBy(item => item.EndLine - item.Line)
                .ThenBy(item => item.EndColumn - item.Column)
                .FirstOrDefault();
            if (function is null || !measured.TryAdd(function, hotspot.Complexity))
                throw new InvalidDataException($"Cannot uniquely match function at {hotspot.Path}:{hotspot.Line}:{hotspot.Column}.");
        }
        if (measured.Count != functions.Count)
            throw new InvalidDataException("The TypeScript analyzer did not measure every function.");
        return functions.Select(function => function with { Complexity = measured[function] }).ToArray();
    }

    private static bool Contains(FunctionMetric function, ComplexityHotspot diagnostic) =>
        function.EndLine == 0
            ? function.Line == diagnostic.Line && function.Column == diagnostic.Column
            : (diagnostic.Line > function.Line || diagnostic.Line == function.Line && diagnostic.Column >= function.Column)
              && (diagnostic.Line < function.EndLine || diagnostic.Line == function.EndLine && diagnostic.Column < function.EndColumn);

    private static bool IsImplementation(ComplexityHotspot hotspot) =>
        SourceCorpus.TryClassify(hotspot.Path, out _, out var role) && role is SourceRole.Production or SourceRole.Tooling;

    private static string Identity(ComplexityHotspot hotspot) =>
        $"{hotspot.Language}\0{hotspot.Path}\0{hotspot.Symbol}";

    private static string Module(string path) => path.StartsWith("tools/Heartbeat.Dev/", StringComparison.Ordinal)
        ? "Developer CLI" : path.StartsWith("src/", StringComparison.Ordinal) ? path.Split('/')[1] : "Repository tooling";

    private static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');

    private static ComplexityQualityReport Unavailable(string reason) => new(false, false, reason, 0, 0, null, BaselineComplete: false, CurrentComplete: false, BaselineCSharpComplete: false, CurrentCSharpComplete: false);

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

    [GeneratedRegex("^(?<path>.+)\\((?<line>\\d+),(?<column>\\d+)\\): warning CA1502: '(?<symbol>[^']+)' has a cyclomatic complexity of '(?<complexity>\\d+)'", RegexOptions.CultureInvariant)]
    private static partial Regex CSharpDiagnostic();

    [GeneratedRegex("complexity of (?<complexity>\\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex ComplexityNumber();

    private sealed record ComplexityScan(
        IReadOnlyList<ComplexityHotspot> Hotspots,
        IReadOnlyList<FunctionMetric> Functions,
        string? Error,
        IReadOnlyList<CouplingHotspot>? Coupling = null,
        bool CSharpComplete = true);
}
