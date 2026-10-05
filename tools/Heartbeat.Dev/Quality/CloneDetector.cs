using System.Text.Json;

namespace Heartbeat.Dev;

/// 重复代码的位置、规模及其相对基点的新增或存量状态。
internal sealed record CloneFinding(
    string FirstFile,
    int FirstStart,
    int FirstEnd,
    string SecondFile,
    int SecondStart,
    int SecondEnd,
    int Lines,
    int Tokens,
    bool IsNew)
{
    public string Describe() =>
        $"{FirstFile}:{FirstStart}-{FirstEnd} <-> {SecondFile}:{SecondStart}-{SecondEnd} "
        + $"({Lines} lines, {Tokens} tokens, {(IsNew ? "new since base" : "stock")})";
}

internal sealed record CloneScan(
    string Scope,
    bool Available,
    string? Unavailable,
    string? ReportDirectory,
    int Clones,
    int NewClones,
    int DuplicatedLines,
    double DuplicatedLinePercentage,
    IReadOnlyList<CloneFinding> NewFindings,
    IReadOnlyList<CloneFinding> StockFindings);

internal sealed record CloneQualityReport(CloneScan Production, CloneScan Tests)
{
    public bool Available => Production.Available && Tests.Available;
    public int NewClones => Production.NewClones + Tests.NewClones;
    public IReadOnlyList<CloneScan> Scans => [Production, Tests];

    public string? Unavailable => Production.Unavailable ?? Tests.Unavailable;
}

/// <summary>
/// 分别扫描生产和测试代码的精确重复，报告位置、规模及相对基点的变化。
/// 闸门只阻止新增重复簇。
/// </summary>
internal sealed class CloneDetector(RepositoryContext repository, IProcessRunner runner)
{
    private const int MinimumLines = 8;
    private const int MinimumTokens = 70;

    public async Task<CloneQualityReport> CompareAsync(
        string baseRef,
        SourceSnapshot baseline,
        SourceSnapshot current,
        string artifactDirectory,
        ICollection<string> commands,
        CancellationToken cancellationToken)
    {
        var toolDirectory = repository.Path("tools", "Heartbeat.Dev", "jscpd");
        var executable = OperatingSystem.IsWindows()
            ? Path.Combine(toolDirectory, "node_modules", ".bin", "jscpd.cmd")
            : Path.Combine(toolDirectory, "node_modules", ".bin", "jscpd");
        if (!File.Exists(executable))
        {
            var missing = $"Run npm --prefix {toolDirectory} ci --ignore-scripts";
            return new CloneQualityReport(Missing("production", missing), Missing("tests", missing));
        }

        var files = baseline.Files.Concat(current.Files).ToArray();
        var production = await ScanAsync(
            executable, "production", Pattern(files, SourceRole.Production), baseRef, artifactDirectory, commands, cancellationToken);
        var tests = await ScanAsync(
            executable, "tests", Pattern(files, SourceRole.Test), baseRef, artifactDirectory, commands, cancellationToken);
        return new CloneQualityReport(production, tests);
    }

    // jscpd applies this pattern to both trees. Keep deleted baseline files and
    // escape literal paths; directory globs would reintroduce a second classifier.
    private static string Pattern(IEnumerable<SourceFileMetric> files, SourceRole role) =>
        "{" + string.Join(',', files.Where(file => file.Role == role).Select(file => file.Path)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Select(path => string.Concat(path.Select(character => "\\[]{}*,?".Contains(character, StringComparison.Ordinal)
                ? "\\" + character : character.ToString())))) + "}";

    private async Task<CloneScan> ScanAsync(
        string executable,
        string scope,
        string pattern,
        string baseRef,
        string artifactDirectory,
        ICollection<string> commands,
        CancellationToken cancellationToken)
    {
        var reportDirectory = Path.Combine(artifactDirectory, $"jscpd-{scope}");
        Directory.CreateDirectory(reportDirectory);
        var configPath = Path.Combine(reportDirectory, "scan-config.json");
        await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(new { pattern }, JsonOptions.Indented), cancellationToken);
        var arguments = new[]
        {
            repository.Root,
            "--config", configPath,
            "--min-lines", MinimumLines.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--min-tokens", MinimumTokens.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--mode", "strict",
            "--reporters", "console,json",
            "--output", reportDirectory,
            "--baseline-from-ref", baseRef,
            "--fail-on-new-clones",
            "--no-colors",
            "--no-tips",
        };
        commands.Add($"jscpd . --config {configPath} --mode strict --min-lines {MinimumLines} --min-tokens {MinimumTokens} "
            + $"--baseline-from-ref {baseRef} --fail-on-new-clones");
        var result = await runner.CaptureAsync(executable, arguments, null, cancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(artifactDirectory, $"jscpd-{scope}.log"),
            result.StdOut + result.StdErr,
            cancellationToken);
        return Read(scope, string.Empty, reportDirectory, result);
    }

    /// jscpd 用退出码 1 表示「有新增重复」，这不是工具故障。只有报告都没写出来才算扫不了。
    internal static CloneScan Read(string scope, string prefix, string reportDirectory, ProcessResult result)
    {
        var reportPath = Path.Combine(reportDirectory, "jscpd-report.json");
        if (!File.Exists(reportPath))
        {
            return new CloneScan(scope, false, $"jscpd could not scan {scope}: {FailureLine(result)}",
                reportDirectory, 0, 0, 0, 0, [], []);
        }
        return Parse(scope, prefix, reportDirectory, File.ReadAllText(reportPath));
    }

    /// 将扫描目录下的相对路径转换为仓库相对路径。
    internal static CloneScan Parse(string scope, string prefix, string? reportDirectory, string json)
    {
        using var document = JsonDocument.Parse(json);
        var total = document.RootElement.GetProperty("statistics").GetProperty("total");
        var findings = document.RootElement.TryGetProperty("duplicates", out var duplicates)
            ? duplicates.EnumerateArray().Select(duplicate => ReadFinding(duplicate, prefix)).ToArray()
            : [];
        return new CloneScan(
            scope,
            true,
            null,
            reportDirectory,
            total.GetProperty("clones").GetInt32(),
            total.TryGetProperty("newClones", out var newClones) ? newClones.GetInt32() : 0,
            total.GetProperty("duplicatedLines").GetInt32(),
            total.GetProperty("percentage").GetDouble(),
            [.. findings.Where(finding => finding.IsNew)],
            [.. findings.Where(finding => !finding.IsNew)]);
    }

    private static CloneFinding ReadFinding(JsonElement duplicate, string prefix)
    {
        var first = duplicate.GetProperty("firstFile");
        var second = duplicate.GetProperty("secondFile");
        return new CloneFinding(
            Qualify(prefix, first.GetProperty("name").GetString()),
            first.GetProperty("start").GetInt32(),
            first.GetProperty("end").GetInt32(),
            Qualify(prefix, second.GetProperty("name").GetString()),
            second.GetProperty("start").GetInt32(),
            second.GetProperty("end").GetInt32(),
            duplicate.GetProperty("lines").GetInt32(),
            duplicate.GetProperty("tokens").GetInt32(),
            duplicate.TryGetProperty("isNew", out var isNew) && isNew.GetBoolean());
    }

    private static string Qualify(string prefix, string? name)
    {
        var normalized = (name ?? "?").Replace('\\', '/');
        return prefix.Length == 0 || normalized.StartsWith(prefix + "/", StringComparison.Ordinal)
            ? normalized
            : $"{prefix}/{normalized}";
    }

    private static CloneScan Missing(string scope, string reason) =>
        new(scope, false, reason, null, 0, 0, 0, 0, [], []);

    /// 优先提取错误信息，避免把末尾耗时行当作失败原因。
    private static string FailureLine(ProcessResult result)
    {
        var lines = (result.StdErr + Environment.NewLine + result.StdOut)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToArray();
        return lines.LastOrDefault(line => line.Contains("error", StringComparison.OrdinalIgnoreCase))
            ?? lines.FirstOrDefault()
            ?? $"exit code {result.ExitCode} without output";
    }
}
