namespace Heartbeat.Dev;

internal sealed class CloneDetector(RepositoryContext repository, IProcessRunner runner)
{
    public async Task<CloneQualityReport> CompareAsync(
        string baseRef,
        string artifactDirectory,
        ICollection<string> commands,
        ICollection<string> artifacts,
        CancellationToken cancellationToken)
    {
        var toolDirectory = repository.Path("tools", "Heartbeat.Dev", "jscpd");
        var executable = OperatingSystem.IsWindows()
            ? Path.Combine(toolDirectory, "node_modules", ".bin", "jscpd.cmd")
            : Path.Combine(toolDirectory, "node_modules", ".bin", "jscpd");
        if (!File.Exists(executable))
        {
            return new CloneQualityReport(false, false, $"Run npm --prefix {toolDirectory} ci --ignore-scripts", null);
        }

        var reportDirectory = Path.Combine(artifactDirectory, "jscpd");
        Directory.CreateDirectory(reportDirectory);
        var arguments = new[]
        {
            repository.Path("src"),
            "--min-lines", "8",
            "--min-tokens", "70",
            "--mode", "strict",
            "--reporters", "console,json",
            "--output", reportDirectory,
            "--ignore", "**/*.Designer.cs,**/*ModelSnapshot.cs,**/*.test.*,**/*.spec.*,**/tests/**,**/package-lock.json,**/*-lock.json,**/next-env.d.ts",
            "--baseline-from-ref", baseRef,
            "--fail-on-new-clones",
            "--no-colors",
            "--no-tips",
        };
        commands.Add($"jscpd src --baseline-from-ref {baseRef} --fail-on-new-clones");
        var result = await runner.CaptureAsync(executable, arguments, null, cancellationToken);
        var logPath = Path.Combine(artifactDirectory, "jscpd.log");
        await File.WriteAllTextAsync(logPath, result.StdOut + result.StdErr, cancellationToken);
        artifacts.Add("jscpd.log");
        artifacts.Add("jscpd/");
        var observationDirectory = Path.Combine(artifactDirectory, "jscpd-observation");
        Directory.CreateDirectory(observationDirectory);
        var observationArguments = new[]
        {
            repository.Path("src"), repository.Path("tests"),
            "--min-lines", "8", "--min-tokens", "70", "--mode", "weak",
            "--ignore-identifiers", "--max-gap-lines", "2", "--similarity", "0.85",
            "--reporters", "json", "--output", observationDirectory,
            "--ignore", "**/*.Designer.cs,**/*ModelSnapshot.cs,**/package-lock.json,**/*-lock.json,**/next-env.d.ts",
            "--no-colors", "--no-tips",
        };
        commands.Add("jscpd src tests --ignore-identifiers --max-gap-lines 2 --similarity 0.85 (report only)");
        var observation = await runner.CaptureAsync(executable, observationArguments, null, cancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(artifactDirectory, "jscpd-observation.log"),
            observation.StdOut + observation.StdErr,
            cancellationToken);
        artifacts.Add("jscpd-observation.log");
        artifacts.Add("jscpd-observation/");
        var metrics = ReadMetrics(Path.Combine(reportDirectory, "jscpd-report.json"));
        var passed = result.ExitCode == 0 && observation.ExitCode == 0;
        return new CloneQualityReport(
            true,
            passed,
            passed ? null : LastUsefulLine(result.ExitCode == 0 ? observation : result),
            reportDirectory,
            metrics?.Clones,
            metrics?.NewClones,
            metrics?.DuplicatedLines,
            metrics?.Percentage,
            observationDirectory);
    }

    private static CloneMetrics? ReadMetrics(string path)
    {
        if (!File.Exists(path)) return null;
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        var total = document.RootElement.GetProperty("statistics").GetProperty("total");
        return new CloneMetrics(
            total.GetProperty("clones").GetInt32(),
            total.GetProperty("newClones").GetInt32(),
            total.GetProperty("duplicatedLines").GetInt32(),
            total.GetProperty("percentage").GetDouble());
    }

    private static string LastUsefulLine(ProcessResult result) =>
        (result.StdErr + Environment.NewLine + result.StdOut)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault()?.Trim() ?? "jscpd failed";

    private sealed record CloneMetrics(int Clones, int NewClones, int DuplicatedLines, double Percentage);
}
