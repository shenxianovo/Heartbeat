namespace Heartbeat.Dev;

/// macOS Collector 的构建入口。native 场景与探针都需要同一份产物，
/// 构建日志一律留在当次证据目录里。
internal static class CollectorBuild
{
    public const string LogName = "collector-build.log";

    public static async Task<string> EnsureAsync(
        RepositoryContext repository,
        IProcessRunner runner,
        ArtifactRun run,
        List<string> commands,
        CancellationToken cancellationToken)
    {
        var projectDirectory = repository.Path("src", "Collectors", "Heartbeat.Collector.Desktop.Mac");
        var project = Path.Combine(projectDirectory, "Heartbeat.Collector.Desktop.Mac.csproj");
        commands.Add("dotnet build src/Collectors/Heartbeat.Collector.Desktop.Mac --no-restore");
        var result = await runner.CaptureAsync(
            "dotnet", ["build", project, "--no-restore", "--verbosity", "minimal"], null, cancellationToken);
        var logPath = Path.Combine(run.Directory, LogName);
        await File.WriteAllTextAsync(logPath, result.StdOut + result.StdErr, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"Collector build failed. See {logPath}.");
        }

        return Path.Combine(projectDirectory, "bin", "Debug", "net10.0", "Heartbeat.Collector.Desktop.Mac.dll");
    }
}
