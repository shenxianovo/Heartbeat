using System.Text.Json;

namespace Heartbeat.Dev;

/// 一处重复：两段代码、各自的行区间、多少行/多少 token，以及相对基点是新增还是存量。
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
/// jscpd 是闸门，不是观察项：生产代码和测试代码各扫一遍精确重复（8 行 / 70 token，strict），
/// 相对基点比较，只有「新增的重复簇」才会挂。挂的时候要说得清是哪两段、多少行、新增还是存量——
/// 以前它只把最后一行输出（往往是 `time: 437ms`）当失败原因，等于没有诊断。
/// </summary>
internal sealed class CloneDetector(RepositoryContext repository, IProcessRunner runner)
{
    private const int MinimumLines = 8;
    private const int MinimumTokens = 70;

    private const string Ignored =
        "**/*.Designer.cs,**/*ModelSnapshot.cs,**/package-lock.json,**/*-lock.json,**/next-env.d.ts";

    public async Task<CloneQualityReport> CompareAsync(
        string baseRef,
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

        var production = await ScanAsync(
            executable, "production", repository.Path("src"), baseRef, artifactDirectory, commands, cancellationToken);
        var tests = await ScanAsync(
            executable, "tests", repository.Path("tests"), baseRef, artifactDirectory, commands, cancellationToken);
        return new CloneQualityReport(production, tests);
    }

    private async Task<CloneScan> ScanAsync(
        string executable,
        string scope,
        string target,
        string baseRef,
        string artifactDirectory,
        ICollection<string> commands,
        CancellationToken cancellationToken)
    {
        var reportDirectory = Path.Combine(artifactDirectory, $"jscpd-{scope}");
        Directory.CreateDirectory(reportDirectory);
        var arguments = new[]
        {
            target,
            "--min-lines", MinimumLines.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--min-tokens", MinimumTokens.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--mode", "strict",
            "--reporters", "console,json",
            "--output", reportDirectory,
            "--ignore", Ignored,
            "--baseline-from-ref", baseRef,
            "--fail-on-new-clones",
            "--no-colors",
            "--no-tips",
        };
        var relative = Path.GetRelativePath(repository.Root, target).Replace('\\', '/');
        commands.Add($"jscpd {relative} --mode strict --min-lines {MinimumLines} --min-tokens {MinimumTokens} "
            + $"--baseline-from-ref {baseRef} --fail-on-new-clones");
        var result = await runner.CaptureAsync(executable, arguments, null, cancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(artifactDirectory, $"jscpd-{scope}.log"),
            result.StdOut + result.StdErr,
            cancellationToken);
        return Read(scope, relative, reportDirectory, result);
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

    /// jscpd 报告里的路径相对被扫的目录，补回仓库前缀，诊断才能直接拿去打开文件。
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

    /// 真正有用的那一行是错误，不是最后一行输出：stdout 的末尾往往是 `time: …ms`。
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
